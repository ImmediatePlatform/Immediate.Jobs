using System.Data;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	/// <inheritdoc />
	public async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
	{
		MergeJobDefinitionsListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		ValidateRegistration(registration);
		await RetryConcurrencyAsync(async connection =>
		{
			if (connection.DataProvider.Name.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
			{
				await MergeSQLiteDefinitionsAsync(connection, registration, cancellationToken);
				return;
			}

			await MergeDefinitionMetadataAsync(connection, registration.Definitions, cancellationToken);
			await MergeRecurringDefinitionsAsync(connection, registration.RecurringSchedules, cancellationToken);
		}, cancellationToken, isolationLevel: IsolationLevel.Serializable);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		GetJobDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await using var scope = contextScope.GetScope(out var connection);
		var definitions = await Definitions(connection).ToListAsync(cancellationToken);
		// The persisted name column is authoritative; serialized metadata may contain a registration alias.
		return [.. definitions.Select(static item => JsonSerializer.Deserialize(item.Metadata, LinqToDBJsonSerializerContext.Default.JobDefinitionRecord)! with { Name = item.Name })
			.OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)];
	}

	private static void ValidateRegistration(JobDefinitionRegistration registration)
	{
		ArgumentNullException.ThrowIfNull(registration);
		ArgumentNullException.ThrowIfNull(registration.Definitions, nameof(registration));
		ArgumentNullException.ThrowIfNull(registration.RecurringSchedules, nameof(registration));
		var definitions = registration.Definitions.ToDictionary(static definition => definition.Name, StringComparer.OrdinalIgnoreCase);
		foreach (var definition in definitions.Values)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name, nameof(registration));
			if (char.IsWhiteSpace(definition.Name[0]) || char.IsWhiteSpace(definition.Name[^1]))
				throw new ArgumentException("Job definition names must not have leading or trailing whitespace.", nameof(registration));
		}

		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var schedule in registration.RecurringSchedules)
		{
			if (!definitions.TryGetValue(schedule.JobName, out var definition)
				|| !schedule.IsCodeDefined
				|| !string.Equals(schedule.Name, definition.Name, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(schedule.Cron, definition.Cron, StringComparison.Ordinal)
				|| !string.Equals(schedule.TimeZone, definition.TimeZone, StringComparison.Ordinal)
				|| !string.Equals(schedule.QueueName, definition.QueueName, StringComparison.Ordinal)
				|| !names.Add(schedule.Name))
				throw new ArgumentException("Code-defined schedules must match their supplied job definitions.", nameof(registration));
		}

		if (definitions.Values.Any(definition => definition.Cron is not null && !names.Contains(definition.Name)))
			throw new ArgumentException("Every managed cron definition must supply its code-defined schedule.", nameof(registration));
	}

	private async Task MergeSQLiteDefinitionsAsync(
		DataConnection connection,
		JobDefinitionRegistration registration,
		CancellationToken cancellationToken
	)
	{
		// SQLite has no SQL MERGE, so reconcile its stored snapshot in the client.
		var definitions = await Definitions(connection).ToListAsync(cancellationToken);
		var schedules = await Recurring(connection).ToListAsync(cancellationToken);
		var changes = JobDefinitionReconciliation.Create(registration,
			definitions.Select(static item => JsonSerializer.Deserialize(item.Metadata, LinqToDBJsonSerializerContext.Default.JobDefinitionRecord)!).ToList(),
			schedules.Select(ToRecord).ToList());
		await Definitions(connection).Where(item => item.Name.In(changes.RemovedDefinitionNames)).DeleteAsync(cancellationToken);
		foreach (var definition in changes.Definitions)
			await connection.InsertOrReplaceAsync(new ImmediateJobDefinitionMetadataEntity
			{
				Name = definition.Name,
				Metadata = JsonSerializer.Serialize(definition, LinqToDBJsonSerializerContext.Default.JobDefinitionRecord),
			}, schemaName: _schema, token: cancellationToken);

		await Recurring(connection).Where(item => item.Name.In(changes.RemovedScheduleNames)).DeleteAsync(cancellationToken);
		foreach (var schedule in changes.Schedules)
			await connection.InsertOrReplaceAsync(ToEntity(schedule), schemaName: _schema, token: cancellationToken);
	}

	private async Task MergeDefinitionMetadataAsync(
		DataConnection connection,
		IReadOnlyList<JobDefinitionRecord> definitions,
		CancellationToken cancellationToken
	)
	{
		var supplied = definitions.Select(static definition => new ImmediateJobDefinitionMetadataEntity
		{
			Name = definition.Name,
			Metadata = JsonSerializer.Serialize(definition, LinqToDBJsonSerializerContext.Default.JobDefinitionRecord),
		}).ToList().AsQueryable(connection);
		// A full join supplies obsolete rows as deletion markers, including an empty startup list.
		// This works with PostgreSQL 15, which has no WHEN NOT MATCHED BY SOURCE clause.
		var source = Definitions(connection).FullJoin(supplied,
			static (stored, item) => stored.Name == item.Name,
			static (stored, item) => new
			{
				Name = stored.Name ?? item.Name,
				item.Metadata,
				Remove = item.Name == null,
			});
		await Definitions(connection).Merge()
			.Using(source)
			.On(static (target, item) => target.Name == item.Name)
			.DeleteWhenMatchedAnd(static (target, item) => item.Remove)
			.UpdateWhenMatched(static (target, item) => new ImmediateJobDefinitionMetadataEntity { Metadata = item.Metadata })
			.InsertWhenNotMatchedAnd(static item => !item.Remove,
				static item => new ImmediateJobDefinitionMetadataEntity { Name = item.Name, Metadata = item.Metadata })
			.MergeAsync(cancellationToken);
	}

	private async Task MergeRecurringDefinitionsAsync(
		DataConnection connection,
		IReadOnlyList<RecurringJobSchedule> schedules,
		CancellationToken cancellationToken
	)
	{
		var supplied = schedules.Select(ToEntity).ToList().AsQueryable(connection);
		var canonical = supplied.Join(Definitions(connection),
			static schedule => schedule.JobName,
			static definition => definition.Name,
			static (schedule, definition) => new ImmediateRecurringJobEntity
			{
				Name = definition.Name,
				JobName = definition.Name,
				QueueName = schedule.QueueName,
				Cron = schedule.Cron,
				TimeZone = schedule.TimeZone,
				IsCodeDefined = true,
				IsPaused = schedule.IsPaused,
				NextRunAt = schedule.NextRunAt,
				LastRunAt = schedule.LastRunAt,
				ConcurrencyStamp = schedule.ConcurrencyStamp,
			});
		var source = Recurring(connection).FullJoin(canonical,
			static (stored, item) => stored.Name == item.Name,
			static (stored, item) => new
			{
				Schedule = new ImmediateRecurringJobEntity
				{
					Name = stored.Name ?? item.Name,
					JobName = item.JobName,
					QueueName = item.QueueName,
					Cron = item.Cron,
					TimeZone = item.TimeZone,
					IsCodeDefined = item.Name != null,
					IsPaused = stored.Name != null ? stored.IsPaused : item.IsPaused,
					LastRunAt = stored.Name != null ? stored.LastRunAt : item.LastRunAt,
					NextRunAt = stored.Name != null && EqualsOrdinal(stored.Cron, item.Cron) && EqualsOrdinal(stored.TimeZone, item.TimeZone)
						? stored.NextRunAt : item.NextRunAt,
					ConcurrencyStamp = item.ConcurrencyStamp,
				},
				WasCodeDefined = stored.IsCodeDefined,
			})
			.Where(static item => item.Schedule.IsCodeDefined || item.WasCodeDefined)
			.Select(static item => item.Schedule);
		await Recurring(connection).Merge()
			.Using(source)
			.OnTargetKey()
			.DeleteWhenMatchedAnd(static (target, item) => !item.IsCodeDefined)
			.UpdateWhenMatched()
			.InsertWhenNotMatchedAnd(static item => item.IsCodeDefined)
			.MergeAsync(cancellationToken);
	}

	[Sql.Expression(ProviderName.SqlServer, "({0} COLLATE Latin1_General_100_BIN2 = {1} COLLATE Latin1_General_100_BIN2 AND DATALENGTH({0}) = DATALENGTH({1}))", ServerSideOnly = true, IsPredicate = true)]
	[Sql.Expression(ProviderName.PostgreSQL, "{0} = {1}", ServerSideOnly = true, IsPredicate = true)]
	private static bool EqualsOrdinal(string left, string right) => throw new InvalidOperationException("This function must be translated to SQL.");

	[LoggerMessage(
		EventId = LibraryEventIds.MergeJobDefinitionsListAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.MergeJobDefinitionsListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MergeJobDefinitionsListAsyncCalled"
	)]
	private partial void MergeJobDefinitionsListAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetJobDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobDefinitionsAsyncCalled"
	)]
	private partial void GetJobDefinitionsAsyncCalled();
}

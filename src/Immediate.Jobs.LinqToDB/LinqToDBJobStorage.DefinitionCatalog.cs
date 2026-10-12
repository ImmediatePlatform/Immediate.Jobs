using System.Data;
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
		return [.. definitions.Select(static item => ToRecord(item))
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
			definitions.Select(ToRecord).ToList(),
			schedules.Select(ToRecord).ToList());
		await Definitions(connection).Where(item => item.Name.In(changes.RemovedDefinitionNames)).DeleteAsync(cancellationToken);
		foreach (var definition in changes.Definitions)
			await connection.InsertOrReplaceAsync(ToEntity(definition), schemaName: _schema, token: cancellationToken);

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
		var supplied = definitions.Select(ToEntity).ToList().AsQueryable(connection);
		var merge = Definitions(connection).Merge()
			.Using(supplied)
			.On(static (dst, src) => dst.Name == src.Name)
			.UpdateWhenMatched(static (dst, src) => new ImmediateJobDefinitionMetadataEntity
			{
				QueueName = src.QueueName,
				QueuePriority = src.QueuePriority,
				QueueConcurrency = src.QueueConcurrency,
				Cron = src.Cron,
				TimeZone = src.TimeZone,
				MaxAttempts = src.MaxAttempts,
				TimeoutTicks = src.TimeoutTicks,
				MaxConcurrency = src.MaxConcurrency,
				OverlapPolicy = src.OverlapPolicy,
				MisfireHandlingMode = src.MisfireHandlingMode,
				Backoff = src.Backoff,
				BackoffBaseTicks = src.BackoffBaseTicks,
			})
			.InsertWhenNotMatched();
		if (connection.DataProvider.Name.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
			await merge.DeleteWhenNotMatchedBySource().MergeAsync(cancellationToken);
		else
		{
			await merge.MergeAsync(cancellationToken);
			// PostgreSQL 15 has no WHEN NOT MATCHED BY SOURCE; remove obsolete rows in the same transaction.
			await Definitions(connection).Where(dst => !supplied.Any(src => dst.Name == src.Name)).DeleteAsync(cancellationToken);
		}
	}

	private async Task MergeRecurringDefinitionsAsync(
		DataConnection connection,
		IReadOnlyList<RecurringJobSchedule> schedules,
		CancellationToken cancellationToken
	)
	{
		var incoming = schedules.Select(ToEntity).ToList().AsQueryable(connection);
		var supplied = incoming.Join(Definitions(connection),
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
		var merge = Recurring(connection).Merge()
			.Using(supplied)
			.On(static (dst, src) => dst.Name == src.Name)
			.UpdateWhenMatched(static (dst, src) => new ImmediateRecurringJobEntity
			{
				JobName = src.JobName,
				QueueName = src.QueueName,
				Cron = src.Cron,
				TimeZone = src.TimeZone,
				IsCodeDefined = true,
				NextRunAt = EqualsOrdinal(dst.Cron, src.Cron) && EqualsOrdinal(dst.TimeZone, src.TimeZone)
					? dst.NextRunAt : src.NextRunAt,
				ConcurrencyStamp = src.ConcurrencyStamp,
			})
			.InsertWhenNotMatched();
		if (connection.DataProvider.Name.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
			await merge.DeleteWhenNotMatchedBySourceAnd(static dst => dst.IsCodeDefined).MergeAsync(cancellationToken);
		else
		{
			await merge.MergeAsync(cancellationToken);
			await Recurring(connection).Where(dst => dst.IsCodeDefined && !supplied.Any(src => dst.Name == src.Name)).DeleteAsync(cancellationToken);
		}
	}

	[Sql.Expression(ProviderName.SqlServer, "({0} COLLATE Latin1_General_100_BIN2 = {1} COLLATE Latin1_General_100_BIN2 AND DATALENGTH({0}) = DATALENGTH({1}))", ServerSideOnly = true, IsPredicate = true)]
	[Sql.Expression(ProviderName.PostgreSQL, "{0} = {1}", ServerSideOnly = true, IsPredicate = true)]
	private static bool EqualsOrdinal(string left, string right) => throw new InvalidOperationException("This function must be translated to SQL.");

	private static ImmediateJobDefinitionMetadataEntity ToEntity(JobDefinitionRecord definition) => new()
	{
		Name = definition.Name,
		QueueName = definition.QueueName,
		QueuePriority = definition.QueuePriority,
		QueueConcurrency = definition.QueueConcurrency,
		Cron = definition.Cron,
		TimeZone = definition.TimeZone,
		MaxAttempts = definition.MaxAttempts,
		TimeoutTicks = definition.Timeout?.Ticks,
		MaxConcurrency = definition.MaxConcurrency,
		OverlapPolicy = definition.OverlapPolicy,
		MisfireHandlingMode = definition.MisfireHandlingMode,
		Backoff = definition.Backoff,
		BackoffBaseTicks = definition.BackoffBase.Ticks,
	};

	private static JobDefinitionRecord ToRecord(ImmediateJobDefinitionMetadataEntity entity) => new()
	{
		Name = entity.Name,
		QueueName = entity.QueueName,
		QueuePriority = entity.QueuePriority,
		QueueConcurrency = entity.QueueConcurrency,
		Cron = entity.Cron,
		TimeZone = entity.TimeZone,
		MaxAttempts = entity.MaxAttempts,
		Timeout = entity.TimeoutTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null,
		MaxConcurrency = entity.MaxConcurrency,
		OverlapPolicy = entity.OverlapPolicy,
		MisfireHandlingMode = entity.MisfireHandlingMode,
		Backoff = entity.Backoff,
		BackoffBase = TimeSpan.FromTicks(entity.BackoffBaseTicks),
	};

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

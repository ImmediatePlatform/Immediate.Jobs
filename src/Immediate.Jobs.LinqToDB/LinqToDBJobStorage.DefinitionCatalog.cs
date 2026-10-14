using System.Text.Json;
using System.Data;
using System.Globalization;
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
		var reports = new List<(string Name, int Jobs, int Schedules)>();
		var terminalGroups = new HashSet<string>(StringComparer.Ordinal);
		await RetryConcurrencyAsync(async connection =>
		{
			reports.Clear();
			terminalGroups.Clear();
			if (connection.DataProvider.Name.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
			{
				await MergeSQLiteDefinitionsAsync(connection, registration, reports, terminalGroups, cancellationToken);
				await RemoveEmptyFairQueueGroupsAsync(connection, cancellationToken);

				return;
			}

			var definitions = registration.Definitions
				.Where(definition => JobTags.Intersect(definition.Tags, registration.ServerTags)).ToList();
			var names = definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var schedules = registration.RecurringSchedules.Where(schedule => names.Contains(schedule.JobName)).ToList();
			var serverTags = registration.ServerTags.ToList().AsQueryable(connection);
			var scheduledNames = schedules.Select(static schedule => schedule.JobName).ToList();
			var includesDefault = registration.ServerTags.Contains(JobTags.Default, StringComparer.OrdinalIgnoreCase);
			var removed = await Definitions(connection).Where(definition => serverTags.Any(tag => HasTag(definition.Tags, tag))
				&& !definition.Name.In(names)).Select(static definition => definition.Name).ToListAsync(cancellationToken);
			await CancelRemovedDefinitionsAsync(connection, removed, reports, terminalGroups, cancellationToken);
			// Evaluate schedule ownership before metadata changes can move or remove its tag scope.
			await Recurring(connection).Where(schedule => schedule.IsCodeDefined
				&& !schedule.JobName.In(scheduledNames) && !schedule.Name.In(scheduledNames)
				&& (schedule.JobName.In(names)
					|| Definitions(connection).Any(definition => definition.Name == schedule.JobName
						&& serverTags.Any(tag => HasTag(definition.Tags, tag)))
					|| (includesDefault && !Definitions(connection).Any(definition => definition.Name == schedule.JobName))))
				.DeleteAsync(cancellationToken);
			await MergeDefinitionMetadataAsync(connection, definitions, serverTags, cancellationToken);
			await MergeRecurringDefinitionsAsync(connection, schedules, cancellationToken);
			await RemoveEmptyFairQueueGroupsAsync(connection, cancellationToken);
		}, cancellationToken, isolationLevel: IsolationLevel.Serializable);
		await CleanupFairQueueGroupsAsync(terminalGroups);
		foreach (var report in reports)
			DefinitionRemoved(report.Name, report.Jobs, report.Schedules);
	}

	private Task<int> RemoveEmptyFairQueueGroupsAsync(DataConnection connection, CancellationToken token) =>
		FairQueueGroups(connection).Where(group => !JobsWithDefinitions(connection).Any(job =>
			job.QueueName == group.QueueName && job.GroupId == group.GroupId
			&& (job.State == JobState.Pending || job.State == JobState.Scheduled || job.State == JobState.Active)))
			.DeleteAsync(token);

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
				|| !names.Add(schedule.Name))
				throw new ArgumentException("Code-defined schedules must match their supplied job definitions.", nameof(registration));
		}

		if (definitions.Values.Any(definition => definition.Cron is not null && !names.Contains(definition.Name)))
			throw new ArgumentException("Every managed cron definition must supply its code-defined schedule.", nameof(registration));
	}

	private async Task MergeSQLiteDefinitionsAsync(
		DataConnection connection,
		JobDefinitionRegistration registration,
		List<(string Name, int Jobs, int Schedules)> reports,
		ISet<string> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		// SQLite has no SQL MERGE, so reconcile its stored snapshot in the client.
		var definitions = await Definitions(connection).ToListAsync(cancellationToken);
		var schedules = await Recurring(connection).ToListAsync(cancellationToken);
		var changes = JobDefinitionReconciliation.Create(registration,
			definitions.Select(ToRecord).ToList(),
			schedules.Select(static schedule => ToRecord(schedule)).ToList());
		await CancelRemovedDefinitionsAsync(connection, changes.RemovedDefinitionNames, reports, terminalGroups, cancellationToken);
		await Definitions(connection).Where(item => item.Name.In(changes.RemovedDefinitionNames)).DeleteAsync(cancellationToken);
		foreach (var definition in changes.Definitions)
			await connection.InsertOrReplaceAsync(ToEntity(definition), schemaName: _schema, token: cancellationToken);

		await Recurring(connection).Where(item => item.Name.In(changes.RemovedScheduleNames)).DeleteAsync(cancellationToken);
		foreach (var schedule in changes.Schedules)
			await connection.InsertOrReplaceAsync(ToEntity(schedule), schemaName: _schema, token: cancellationToken);
	}

	private async Task CancelRemovedDefinitionsAsync(DataConnection connection, IReadOnlyList<string> names,
		List<(string Name, int Jobs, int Schedules)> reports, ISet<string> terminalGroups,
		CancellationToken cancellationToken)
	{
		if (names.Count == 0)
			return;

		var query = Jobs(connection).Where(job => job.State != JobState.Succeeded && job.State != JobState.Failed
			&& job.State != JobState.Cancelled && job.State != JobState.Skipped && job.JobName.In(names));
		var schedulesQuery = Recurring(connection).Where(schedule => !schedule.IsCodeDefined
			&& schedule.CancellationReason == null && schedule.JobName.In(names));
		var jobs = await query.ToListAsync(cancellationToken);
		var schedules = await schedulesQuery.ToListAsync(cancellationToken);
		var now = timeProvider.GetUtcNow();
		foreach (var job in jobs)
			await MarkCancelledAsync(connection, job, now, JobDefinitionReconciliation.RemovalCancellationReason, cancellationToken);
		foreach (var job in jobs)
			await PropagateTerminalAsync(connection, job, now, terminalGroups, cancellationToken);
		foreach (var schedule in schedules)
			await Recurring(connection).Where(item => item.Name == schedule.Name)
				.Set(item => item.CancellationReason, JobDefinitionReconciliation.RemovalCancellationReason)
				.Set(item => item.IsPaused, value: true).Set(item => item.ConcurrencyStamp, Guid.NewGuid()).UpdateAsync(cancellationToken);
		foreach (var name in names)
			reports.Add((name, jobs.Count(job => string.Equals(job.JobName, name, StringComparison.OrdinalIgnoreCase)),
				schedules.Count(schedule => string.Equals(schedule.JobName, name, StringComparison.OrdinalIgnoreCase))));
	}

	private async Task MergeDefinitionMetadataAsync(
		DataConnection connection,
		IReadOnlyList<JobDefinitionRecord> definitions,
		IQueryable<string> serverTags,
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
				Timeout = src.Timeout,
				MaxConcurrency = src.MaxConcurrency,
				OverlapPolicy = src.OverlapPolicy,
				MisfireHandlingMode = src.MisfireHandlingMode,
				Backoff = src.Backoff,
				BackoffBase = src.BackoffBase,
				SlidingWindowMax = src.SlidingWindowMax,
				SlidingWindowPeriod = src.SlidingWindowPeriod,
				FixedWindowMax = src.FixedWindowMax,
				FixedWindowPeriod = src.FixedWindowPeriod,
				Tags = src.Tags,
			})
			.InsertWhenNotMatched();
		if (connection.DataProvider.Name.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
			await merge.DeleteWhenNotMatchedBySourceAnd(dst => serverTags.Any(tag => HasTag(dst.Tags, tag))).MergeAsync(cancellationToken);
		else
		{
			await merge.MergeAsync(cancellationToken);
			// PostgreSQL 15 has no WHEN NOT MATCHED BY SOURCE; remove obsolete rows in the same transaction.
			await Definitions(connection).Where(dst => serverTags.Any(tag => HasTag(dst.Tags, tag))
				&& !supplied.Any(src => dst.Name == src.Name)).DeleteAsync(cancellationToken);
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
				CancellationReason = schedule.CancellationReason,
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
				Cron = src.Cron,
				TimeZone = src.TimeZone,
				IsCodeDefined = true,
				NextRunAt = EqualsOrdinal(dst.Cron, src.Cron) && EqualsOrdinal(dst.TimeZone, src.TimeZone)
					? dst.NextRunAt : src.NextRunAt,
				ConcurrencyStamp = src.ConcurrencyStamp,
			})
			.InsertWhenNotMatched();
		await merge.MergeAsync(cancellationToken);
	}

	[Sql.Expression(ProviderName.SqlServer, "EXISTS (SELECT 1 FROM OPENJSON(CAST({0} AS nvarchar(max))) jsonTag WHERE jsonTag.[value] COLLATE Latin1_General_100_CI_AS = {1} COLLATE Latin1_General_100_CI_AS AND DATALENGTH(jsonTag.[value]) = DATALENGTH({1}))", ServerSideOnly = true, IsPredicate = true)]
	[Sql.Expression(ProviderName.PostgreSQL, "EXISTS (SELECT 1 FROM jsonb_array_elements_text(CAST({0} AS jsonb)) AS jsonTag(value) WHERE jsonTag.value COLLATE \"immediate_jobs_case_insensitive\" = {1} COLLATE \"immediate_jobs_case_insensitive\")", ServerSideOnly = true, IsPredicate = true)]
	private static bool HasTag(string tags, string tag) => throw new InvalidOperationException("This function must be translated to SQL.");

	[Sql.Expression(ProviderName.SqlServer, "({0} COLLATE Latin1_General_100_BIN2 = {1} COLLATE Latin1_General_100_BIN2 AND DATALENGTH({0}) = DATALENGTH({1}))", ServerSideOnly = true, IsPredicate = true)]
	[Sql.Expression(ProviderName.PostgreSQL, "{0} = {1}", ServerSideOnly = true, IsPredicate = true)]
	[Sql.Expression(ProviderName.SQLite, "{0} COLLATE BINARY = {1} COLLATE BINARY", ServerSideOnly = true, IsPredicate = true)]
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
		Timeout = definition.Timeout?.ToString(),
		MaxConcurrency = definition.MaxConcurrency,
		OverlapPolicy = definition.OverlapPolicy,
		MisfireHandlingMode = definition.MisfireHandlingMode,
		Backoff = definition.Backoff,
		BackoffBase = definition.BackoffBase.ToString(),
		SlidingWindowMax = definition.SlidingWindowMax,
		SlidingWindowPeriod = definition.SlidingWindowPeriod?.ToString(),
		FixedWindowMax = definition.FixedWindowMax,
		FixedWindowPeriod = definition.FixedWindowPeriod?.ToString(),
		Tags = JsonSerializer.Serialize([.. definition.Tags], LinqToDBJsonSerializerContext.Default.StringArray),
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
		Timeout = entity.Timeout is { } timeout ? TimeSpan.Parse(timeout, CultureInfo.InvariantCulture) : null,
		MaxConcurrency = entity.MaxConcurrency,
		OverlapPolicy = entity.OverlapPolicy,
		MisfireHandlingMode = entity.MisfireHandlingMode,
		Backoff = entity.Backoff,
		BackoffBase = TimeSpan.Parse(entity.BackoffBase, CultureInfo.InvariantCulture),
		SlidingWindowMax = entity.SlidingWindowMax,
		SlidingWindowPeriod = entity.SlidingWindowPeriod is { } slidingPeriod ? TimeSpan.Parse(slidingPeriod, CultureInfo.InvariantCulture) : null,
		FixedWindowMax = entity.FixedWindowMax,
		FixedWindowPeriod = entity.FixedWindowPeriod is { } fixedPeriod ? TimeSpan.Parse(fixedPeriod, CultureInfo.InvariantCulture) : null,
		Tags = JsonSerializer.Deserialize(entity.Tags, LinqToDBJsonSerializerContext.Default.StringArray) ?? [],
	};

	[LoggerMessage(
		EventId = LibraryEventIds.DefinitionRemoved,
		EventName = "Immediate.Jobs.LinqToDB.DefinitionRemoved",
		Level = LogLevel.Warning,
		Message = "Job definition '{JobName}' removed; canceled {JobCount} invocations and {ScheduleCount} recurring schedules."
	)]
	private partial void DefinitionRemoved(string jobName, int jobCount, int scheduleCount);

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

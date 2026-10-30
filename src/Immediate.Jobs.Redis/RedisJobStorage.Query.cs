using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	private const int QueryWindowSize = 256;

	private const int MaximumQueryTake = 1000;

	/// <inheritdoc />
	public async ValueTask<JobMonitoringDefinitions> GetMonitoringDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		GetMonitoringDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		var snapshot = await ReadDefinitionCatalogAsync(cancellationToken);
		return new()
		{
			Jobs = ReadDefinitionRecords(snapshot[1]),
			Recurring = [.. ReadRecurringRecords(snapshot[2]).Select(static schedule => schedule.ToDefinition())],
		};
	}

	private async ValueTask<IReadOnlyList<JobAcquisitionState>?> ReadDefinitionStatusesAsync(
		IReadOnlyList<JobDefinitionRecord> definitions,
		string version,
		CancellationToken cancellationToken
	)
	{
		if (definitions.Count == 0)
			return [];
		var now = _timeProvider.GetUtcNow();
		var arguments = new List<RedisValue> { _root, Score(now), Timestamp(now), version };
		foreach (var definition in definitions)
		{
			arguments.Add(definition.Name.ToUpperInvariant());
			AddDefinitionLimits(arguments, definition.AcquisitionLimits, now);
		}

		var response = await Database.ScriptEvaluateAsync(
			RedisScripts.DefinitionStates, [DefinitionCatalogVersionKey], [.. arguments]
		).WaitAsync(cancellationToken);
		if (response.IsNull)
			return null;

		var results = (RedisResult[])response!;
		return [.. definitions.Select((definition, index) =>
			ToAcquisitionState(definition.Name, definition.AcquisitionLimits, (RedisResult[])results[index]!))];
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(
		CancellationToken cancellationToken = default
	)
	{
		GetMonitoringSnapshotAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var states = Enum.GetValues<JobState>();
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var now = _timeProvider.GetUtcNow();
			var snapshot = (RedisResult[])(await Database.ScriptEvaluateAsync(
				RedisScripts.ReadMonitoring,
				[DefinitionCatalogVersionKey, DefinitionMetadataKey, RecurringNamesKey, ServersKey, .. states.Select(StateKey)],
				[_root, Score(now)]
			).WaitAsync(cancellationToken))!;
			var definitions = ReadDefinitionRecords(snapshot[1]);
			var statuses = await ReadDefinitionStatusesAsync(definitions, (string)snapshot[0]!, cancellationToken);
			// Limits are evaluated in C#; retry rather than apply stale limits to a changed catalogue.
			if (statuses is null)
				continue;

			var counts = (RedisResult[])snapshot[4]!;
			return new JobMonitoringSnapshot
			{
				CapturedAt = now,
				Counts = states.Select((state, index) => KeyValuePair.Create(state, (long)counts[index])).ToDictionary(),
				Recurring = [.. ReadRecurringRecords(snapshot[2]).Select(static schedule => schedule.ToStatus())],
				DefinitionStatuses = statuses,
				Servers = [.. ReadValues(snapshot[3]).Select(static value =>
					JsonSerializer.Deserialize((string)value!, RedisJsonSerializerContext.Default.JobServerSnapshot)!)],
				Capabilities = this.GetCapabilities(),
			};
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryJobsAsyncCalled(query);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		if (query.JobHandle is { } id)
		{
			var job = await ReadJobAsync(id, cancellationToken);
			return job is not null && query.Skip == 0 && MatchesQuery(job, query) ? [job] : [];
		}

		var take = Math.Min(query.Take, MaximumQueryTake);
		if (!HasFilters(query))
		{
			var (_, jobs) = await ReadJobPageAsync(query.Skip, take, query, cancellationToken);
			return jobs;
		}

		var matchLimit = (long)query.Skip + take;
		var matches = new List<JobRecord>(take);
		long rank = 0;
		long matched = 0;
		while (matched < matchLimit)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (scanned, jobs) = await ReadJobPageAsync(rank, QueryWindowSize, query, cancellationToken);
			if (scanned == 0)
				break;

			foreach (var job in jobs)
			{
				if (!MatchesQuery(job, query))
					continue;
				if (matched++ >= query.Skip)
					matches.Add(job);
				if (matched == matchLimit)
					break;
			}

			rank += scanned;
			if (scanned < QueryWindowSize)
				break;
		}

		return matches;
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryNonCompletedJobsAsync(
		string jobName,
		CancellationToken cancellationToken = default
	)
	{
		QueryNonCompletedJobsAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		JobState[] states =
		[
			JobState.AwaitingContinuation,
			JobState.WaitingForTrigger,
			JobState.Scheduled,
			JobState.Pending,
			JobState.Active,
		];
		var result = await Database.ScriptEvaluateAsync(
			RedisScripts.ReadNonCompletedJobs,
			[DefinitionMetadataKey, .. states.Select(StateKey)],
			[_root, jobName.ToUpperInvariant()]
		).WaitAsync(cancellationToken);
		return [.. ReadJobRecords(result).Where(job => string.Equals(job.JobName, jobName, StringComparison.OrdinalIgnoreCase))];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobHandle,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryJobExecutionsAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var response = (RedisResult[])(await Database.ScriptEvaluateAsync(
			RedisScripts.ReadJobExecutions,
			[JobKey(jobHandle), ExecutionIndexKey(jobHandle), ExecutionDataKey(jobHandle), DefinitionMetadataKey],
			[_root, query.Skip, Math.Min(query.Take, MaximumQueryTake), query.Attempt is { } attempt ? (RedisValue)attempt : "",
				.. ExecutionFieldNames.Select(static field => (RedisValue)field)]
		).WaitAsync(cancellationToken))!;
		if (response.Length == 0)
			return [];

		var result = new List<JobExecutionRecord>();
		if ((long)response[1] == 1)
			result.Add(JobExecutionRecord.CreateSynthetic(ToJobRecord(ReadValues(response[0])))!);

		result.AddRange(((RedisResult[])response[2]!).Select(item => ToExecutionRecord(jobHandle, ReadValues(item))));
		return result;
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobHandle,
		CancellationToken cancellationToken = default
	)
	{
		GetJobStatusAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var job = await ReadJobAsync(jobHandle, cancellationToken);
		return job is null
			? null
			: new JobStatus
			{
				JobHandle = job.JobHandle,
				JobName = job.JobName,
				QueueName = job.QueueName,
				CancellationReason = job.CancellationReason,
				State = job.State,
				Attempt = job.Attempt,
				MaxAttempts = 0,
				CreatedAt = job.CreatedAt,
				DueAt = job.DueAt,
				CompletedAt = job.CompletedAt,
				LastError = job.LastError,
				BatchHandle = null,
				DependsOn = [],
			};
	}

	private async Task<(long Scanned, IReadOnlyList<JobRecord> Jobs)> ReadJobPageAsync(
		long start,
		int count,
		JobQuery query,
		CancellationToken cancellationToken
	)
	{
		var result = (RedisResult[])(await Database.ScriptEvaluateAsync(
			RedisScripts.ReadJobPage,
			[AllJobsKey, DefinitionMetadataKey],
			[_root, start, start + count - 1, query.State is { } state ? (RedisValue)(int)state : "",
				string.IsNullOrWhiteSpace(query.JobName) ? "" : query.JobName.ToUpperInvariant(), NullableTimestamp(query.CreatedBefore)]
		).WaitAsync(cancellationToken))!;
		return ((long)result[0], ReadJobRecords(result[1]));
	}

	private static bool HasFilters(JobQuery query) =>
		query.State is not null ||
		query.CreatedBefore is not null ||
		!string.IsNullOrWhiteSpace(query.QueueName) ||
		!string.IsNullOrWhiteSpace(query.JobName) ||
		!string.IsNullOrWhiteSpace(query.Search);

	private static bool MatchesQuery(JobRecord job, JobQuery query) =>
		(query.State is not { } state || job.State == state) &&
		(query.CreatedBefore is not { } createdBefore || job.CreatedAt < createdBefore) &&
		(string.IsNullOrWhiteSpace(query.QueueName) || string.Equals(job.QueueName, query.QueueName, StringComparison.Ordinal)) &&
		(string.IsNullOrWhiteSpace(query.JobName) || string.Equals(job.JobName, query.JobName, StringComparison.OrdinalIgnoreCase)) &&
		(string.IsNullOrWhiteSpace(query.Search) ||
			job.JobName.Contains(query.Search, StringComparison.OrdinalIgnoreCase));

	[LoggerMessage(
		EventId = LibraryEventIds.GetMonitoringSnapshotAsyncCalled,
		EventName = "Immediate.Jobs.Redis.GetMonitoringSnapshotAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetMonitoringSnapshotAsync called"
	)]
	private partial void GetMonitoringSnapshotAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.QueryJobsAsyncCalled,
		EventName = "Immediate.Jobs.Redis.QueryJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobsAsync called (Query={Query})"
	)]
	private partial void QueryJobsAsyncCalled(JobQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryNonCompletedJobsAsyncCalled,
		EventName = "Immediate.Jobs.Redis.QueryNonCompletedJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryNonCompletedJobsAsyncCalled called (JobName={JobName})"
	)]
	private partial void QueryNonCompletedJobsAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryJobExecutionsAsyncCalled,
		EventName = "Immediate.Jobs.Redis.QueryJobExecutionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobExecutionsAsync called (JobHandle={JobHandle})"
	)]
	private partial void QueryJobExecutionsAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobStatusAsyncCalled,
		EventName = "Immediate.Jobs.Redis.GetJobStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobStatusAsync called (JobHandle={JobHandle})"
	)]
	private partial void GetJobStatusAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetMonitoringDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.Redis.GetMonitoringDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetMonitoringDefinitionsAsync called"
	)]
	private partial void GetMonitoringDefinitionsAsyncCalled();
}

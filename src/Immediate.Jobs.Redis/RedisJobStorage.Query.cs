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
		var recurring = await ReadAllRecurringAsync(cancellationToken);
		return new()
		{
			Jobs = await GetJobDefinitionsAsync(cancellationToken),
			Recurring = [.. recurring.Select(static schedule => schedule.ToDefinition())],
		};
	}

	private async ValueTask<IReadOnlyList<JobAcquisitionState>> ReadDefinitionStatusesAsync(CancellationToken cancellationToken)
	{
		var definitions = await GetJobDefinitionsAsync(cancellationToken);
		if (definitions.Count == 0)
			return [];
		var now = _timeProvider.GetUtcNow();
		var arguments = new List<RedisValue> { _root, Score(now), Timestamp(now) };
		foreach (var definition in definitions)
		{
			arguments.Add(definition.Name.ToUpperInvariant());
			AddDefinitionLimits(arguments, definition.AcquisitionLimits, now);
		}

		var results = (RedisResult[])(await Database.ScriptEvaluateAsync(RedisScripts.DefinitionStates, [LeasesKey], [.. arguments]).WaitAsync(cancellationToken))!;
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
		var countTasks = states
			.Select(state => Database.SetLengthAsync(StateKey(state)))
			.ToList();
		var recurringTask = ReadAllRecurringAsync(cancellationToken);
		var serversTask = ReadLiveServersAsync(cancellationToken);
		await Task.WhenAll(countTasks).WaitAsync(cancellationToken);
		var recurring = await recurringTask;
		var servers = await serversTask;
		var counts = states
			.Select((state, index) => KeyValuePair.Create(state, countTasks[index].Result))
			.ToDictionary();
		return new JobMonitoringSnapshot
		{
			CapturedAt = _timeProvider.GetUtcNow(),
			Counts = counts,
			Recurring = [.. recurring.Select(static schedule => schedule.ToStatus())],
			DefinitionStatuses = await ReadDefinitionStatusesAsync(cancellationToken),
			Servers = servers,
			Capabilities = this.GetCapabilities(),
		};
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
		var members = await Task.WhenAll(states.Select(state => Database.SetMembersAsync(StateKey(state))))
			.WaitAsync(cancellationToken);
		var ids = members.SelectMany(static set => set)
			.Select(static value => (string)value!)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		var jobs = await ReadJobsAsync(ids, cancellationToken);
		return
		[
			.. jobs.Where(job => string.Equals(job.JobName, jobName, StringComparison.OrdinalIgnoreCase)
				&& job.State is (JobState.AwaitingContinuation or JobState.WaitingForTrigger
					or JobState.Scheduled or JobState.Pending or JobState.Active)),
		];
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

		var values = await ReadJobValuesAsync(jobHandle, cancellationToken);
		if (values is null)
			return null;

		var header = JsonSerializer.Deserialize((byte[])values[0]!, RedisJsonSerializerContext.Default.RedisJobHeader)
			?? throw new ImmediateJobException($"Job '{jobHandle}' contains invalid data.");
		return new()
		{
			JobHandle = header.JobHandle,
			JobName = header.JobName,
			QueueName = NullIfEmpty(values[12]),
			CancellationReason = values[11].IsNull ? header.CancellationReason : NullIfEmpty(values[11]),
			State = (JobState)ParseInt32(values[1]),
			Attempt = ParseInt32(values[3]),
			MaxAttempts = 0,
			CreatedAt = header.CreatedAt,
			DueAt = FromTimestamp(values[2]),
			CompletedAt = FromNullableTimestamp(values[7]),
			LastError = NullIfEmpty(values[6]),
			BatchHandle = null,
			DependsOn = [],
		};
	}

	private async Task<IReadOnlyList<JobServerSnapshot>> ReadLiveServersAsync(CancellationToken cancellationToken)
	{
		var now = _timeProvider.GetUtcNow();
		var stale = await Database.SortedSetRangeByScoreAsync(
			ServersKey,
			stop: Score(now),
			exclude: Exclude.Stop
		).WaitAsync(cancellationToken);
		if (stale.Length != 0)
			await Database.SortedSetRemoveAsync(ServersKey, stale).WaitAsync(cancellationToken);
		var ids = await Database.SortedSetRangeByScoreAsync(
			ServersKey,
			start: Score(now)
		).WaitAsync(cancellationToken);
		var tasks = ids
			.Select(id => Database.HashGetAsync(ServerKey((string)id!), ["last", "active", "max", "expires", "details"]))
			.ToList();
		await Task.WhenAll(tasks).WaitAsync(cancellationToken);
		return
		[
			.. tasks
				.Select((task, index) => (Id: (string)ids[index]!, Values: task.Result))
				.Where(static server => !server.Values[4].IsNullOrEmpty)
				.Select(static server => JsonSerializer.Deserialize((string)server.Values[4]!, RedisJsonSerializerContext.Default.JobServerSnapshot)!),
		];
	}

	private async Task<IReadOnlyList<JobRecord>> ReadJobsAsync(
		IReadOnlyList<string> ids,
		CancellationToken cancellationToken
	)
	{
		var records = await ReadJobsDataAsync(ids, cancellationToken);
		var queues = await ReadDefinitionQueuesAsync(records.Select(static job => job.JobName), cancellationToken);
		return [.. records.Select(job => job with { QueueName = queues[job.JobName] })];
	}

	private async Task<IReadOnlyList<JobRecord>> ReadJobsDataAsync(
		IReadOnlyList<string> ids,
		CancellationToken cancellationToken
	)
	{
		var tasks = ids.Select(id => ReadJobDataAsync(new() { Value = id }, cancellationToken).AsTask()).ToList();
		var jobs = await Task.WhenAll(tasks).WaitAsync(cancellationToken);
		return [.. jobs.OfType<JobRecord>()];
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

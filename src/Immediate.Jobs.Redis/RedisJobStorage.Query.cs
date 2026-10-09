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
		var arguments = new List<RedisValue> { _root, Score(now), Ticks(now) };
		foreach (var definition in definitions)
		{
			arguments.Add(definition.Name.ToUpperInvariant());
			AddDefinitionLimits(arguments, definition.AcquisitionLimits, now);
		}

		var results = (RedisResult[])(await Database.ScriptEvaluateAsync(RedisScripts.DefinitionStates, [LeasesKey], [.. arguments]).WaitAsync(cancellationToken))!;
		return [.. definitions.Select((definition, index) =>
		{
			var result = (RedisResult[])results[index]!;
			var next = (string)result[1]!;
			var active = (int)(long)result[3];
			return new JobAcquisitionState
			{
				JobName = definition.Name,
				IsPaused = (long)result[2] == 1,
				ActiveCount = active,
				IsConcurrencyLimited = definition.MaxConcurrency > 0 && active >= definition.MaxConcurrency,
				AcquisitionStatus = (JobAcquisitionStatus)(long)result[0],
				NextEligibleAt = next.Length == 0 ? null : new DateTimeOffset(long.Parse(next, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero),
			};
		})];
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
		_ = await Task.WhenAll(countTasks).WaitAsync(cancellationToken);
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
			var ids = await ReadJobHandlesByRankAsync(query.Skip, take, cancellationToken);
			return await ReadJobsAsync(ids, cancellationToken);
		}

		var matchLimit = (long)query.Skip + take;
		var matches = new List<JobRecord>(take);
		long rank = 0;
		long matched = 0;
		while (matched < matchLimit)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var ids = await ReadJobHandlesByRankAsync(rank, QueryWindowSize, cancellationToken);
			if (ids.Count == 0)
				break;

			var jobs = await ReadJobsAsync(ids, cancellationToken);
			foreach (var job in jobs)
			{
				if (!MatchesQuery(job, query))
					continue;
				if (matched++ >= query.Skip)
					matches.Add(job);
				if (matched == matchLimit)
					break;
			}

			rank += ids.Count;
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

		var job = await ReadJobAsync(jobHandle, cancellationToken);
		if (job is null)
			return [];

		var synthetic = JobExecutionRecord.CreateSynthetic(job);
		var syntheticMissing = synthetic is not null
			&& (query.Attempt is null || query.Attempt == synthetic.Attempt)
			&& !await Database.HashExistsAsync(
				ExecutionDataKey(jobHandle),
				ExecutionField(synthetic.Attempt, "state")
			).WaitAsync(cancellationToken);
		var skip = query.Skip;
		var take = Math.Min(query.Take, MaximumQueryTake);
		var result = new List<JobExecutionRecord>(take);
		if (syntheticMissing && skip == 0)
		{
			result.Add(synthetic!);
			take--;
		}
		else if (syntheticMissing)
		{
			skip--;
		}

		if (take == 0 || skip < 0)
			return result;

		RedisValue[] attempts;
		if (query.Attempt is { } attempt)
		{
			var exists = await Database.HashExistsAsync(
				ExecutionDataKey(jobHandle),
				ExecutionField(attempt, "state")
			).WaitAsync(cancellationToken);
			attempts = exists && skip == 0 ? [attempt] : [];
		}
		else
		{
			attempts = await Database.SortedSetRangeByRankAsync(
				ExecutionIndexKey(jobHandle),
				skip,
				skip + take - 1,
				Order.Descending
			).WaitAsync(cancellationToken);
		}

		result.AddRange(await ReadExecutionsAsync(jobHandle, attempts, cancellationToken));
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

	private async Task<IReadOnlyList<JobServerSnapshot>> ReadLiveServersAsync(CancellationToken cancellationToken)
	{
		var now = _timeProvider.GetUtcNow();
		var stale = await Database.SortedSetRangeByScoreAsync(
			ServersKey,
			stop: Score(now),
			exclude: Exclude.Stop
		).WaitAsync(cancellationToken);
		if (stale.Length != 0)
			_ = await Database.SortedSetRemoveAsync(ServersKey, stale).WaitAsync(cancellationToken);
		var ids = await Database.SortedSetRangeByScoreAsync(
			ServersKey,
			start: Score(now)
		).WaitAsync(cancellationToken);
		var tasks = ids
			.Select(id => Database.HashGetAsync(ServerKey((string)id!), ["last", "active", "max", "expires", "details"]))
			.ToList();
		_ = await Task.WhenAll(tasks).WaitAsync(cancellationToken);
		return
		[
			.. tasks
				.Select((task, index) => (Id: (string)ids[index]!, Values: task.Result))
				.Where(static server => !server.Values[0].IsNullOrEmpty)
				.Select(static server => server.Values[4] is { IsNullOrEmpty: false }
					? JsonSerializer.Deserialize((string)server.Values[4]!, RedisJsonSerializerContext.Default.JobServerSnapshot)!
					: new JobServerSnapshot
				{
					WorkerId = server.Id,
					LastHeartbeat = FromTicks(server.Values[0]),
					ActiveWorkers = ParseInt32(server.Values[1]),
					MaxWorkers = ParseInt32(server.Values[2]),
					ServerTimeout = FromTicks(server.Values[3]) - FromTicks(server.Values[0]),
				}),
		];
	}

	private async Task<IReadOnlyList<JobRecord>> ReadJobsAsync(
		IReadOnlyList<string> ids,
		CancellationToken cancellationToken
	)
	{
		var tasks = ids.Select(id => ReadJobAsync(new() { Value = id }, cancellationToken).AsTask()).ToList();
		var jobs = await Task.WhenAll(tasks).WaitAsync(cancellationToken);
		return [.. jobs.OfType<JobRecord>()];
	}

	private async Task<IReadOnlyList<JobExecutionRecord>> ReadExecutionsAsync(
		JobHandle jobHandle,
		RedisValue[] attempts,
		CancellationToken cancellationToken
	)
	{
		if (attempts.Length == 0)
			return [];

		var fields = new RedisValue[attempts.Length * ExecutionFieldNames.Length];
		for (var index = 0; index < attempts.Length; index++)
		{
			var executionNumber = ParseInt32(attempts[index]);
			for (var fieldIndex = 0; fieldIndex < ExecutionFieldNames.Length; fieldIndex++)
			{
				fields[(index * ExecutionFieldNames.Length) + fieldIndex] =
					ExecutionField(executionNumber, ExecutionFieldNames[fieldIndex]);
			}
		}

		var allValues = await Database.HashGetAsync(ExecutionDataKey(jobHandle), fields)
			.WaitAsync(cancellationToken);

		var executions = new List<JobExecutionRecord>(attempts.Length);
		for (var index = 0; index < attempts.Length; index++)
		{
			var values = allValues.AsSpan(index * ExecutionFieldNames.Length, ExecutionFieldNames.Length);
			if (values[0].IsNull)
				continue;
			executions.Add(new()
			{
				JobHandle = jobHandle,
				Attempt = ParseInt32(attempts[index]),
				State = (JobExecutionState)ParseInt32(values[0]),
				WorkerId = NullIfEmpty(values[1]),
				AcquiredAt = FromNullableTicks(values[2]),
				ExecutionStartedAt = FromNullableTicks(values[3]),
				CompletedAt = FromNullableTicks(values[4]),
				ExecutionTraceId = NullIfEmpty(values[5]),
				ExecutionSpanId = NullIfEmpty(values[6]),
				Error = NullIfEmpty(values[7]),
				IsSynthetic = values[8] == "1",
			});
		}

		return executions;
	}

	private async Task<IReadOnlyList<string>> ReadJobHandlesByRankAsync(
		long start,
		int count,
		CancellationToken cancellationToken
	)
	{
		var values = await Database.SortedSetRangeByRankAsync(
			AllJobsKey,
			start,
			start + count - 1,
			Order.Descending
		).WaitAsync(cancellationToken);
		return [.. values.Select(static value => (string)value!)];
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

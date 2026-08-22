using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

/// <summary>
/// Distributed Redis storage for ordinary queue jobs and recurring schedules.
/// Batches and continuations require a graph-capable SQL provider.
/// </summary>
internal sealed class RedisJobStorage(
	IConnectionMultiplexer connection,
	IOptions<RedisJobStorageOptions> options,
	TimeProvider timeProvider
) : IRecurringJobStorage
{
	private const int QueryWindowSize = 256;
	private const int MaximumQueryTake = 1000;

	private static readonly RedisValue[] JobMutableFields =
	[
		"record",
		"state",
		"due",
		"attempt",
		"worker",
		"lease",
		"error",
		"completed",
		"executionTraceId",
		"executionSpanId",
		"executionStartedAt",
	];

	private static readonly RedisValue[] RecurringMutableFields = ["record", "paused", "next", "last"];
	private static readonly string[] ExecutionFieldNames =
	[
		"state",
		"worker",
		"acquired",
		"started",
		"completed",
		"trace",
		"span",
		"error",
		"synthetic",
	];

	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Owned by DI")]
	private readonly IConnectionMultiplexer _connection = connection;

	private readonly RedisJobStorageOptions _storageOptions = options.Value;
	private readonly TimeProvider _timeProvider = timeProvider;
	private readonly string _root = $"{{{options.Value.KeyPrefix}}}:";

	private IDatabase Database => _connection.GetDatabase(_storageOptions.Database);

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await Database.PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		ValidateQueueJob(job);
		var result = await EvaluateInt64Async(
			RedisScripts.Enqueue,
			[JobKey(job.JobId), AllJobsKey, StateKey(job.State), DueKey(job.QueueName)],
			CreateEnqueueArguments(job),
			cancellationToken
		).ConfigureAwait(false);
		if (result == 0)
			throw new ImmediateJobException($"Job '{job.JobId}' already exists.");
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (request.FairQueues is not null)
		{
			throw new NotSupportedException(
				"Direct distributed fair-queue acquisition is not supported by the Redis provider."
			);
		}

		var keys = new List<RedisKey>(4 + request.Queues.Count)
		{
			LeasesKey,
			StateKey(JobState.Active),
			StateKey(JobState.Pending),
			StateKey(JobState.Scheduled),
		};
		var now = _timeProvider.GetUtcNow();
		var leaseExpiresAt = now + request.Lease;
		var values = new List<RedisValue>
		{
			Score(now),
			Score(leaseExpiresAt),
			Ticks(leaseExpiresAt),
			request.WorkerId,
			request.BatchSize,
			request.Queues.Count,
			_root,
			Ticks(now),
		};
		foreach (var queue in request.Queues)
		{
			keys.Add(DueKey(queue.QueueName));
			values.Add(queue.QueueName);
			values.Add(Math.Max(0, queue.Capacity));
			values.Add(queue.JobCapacities.Count);
			foreach (var capacity in queue.JobCapacities)
			{
				values.Add(capacity.Key);
				values.Add(Math.Max(0, capacity.Value));
			}
		}

		var result = await Database.ScriptEvaluateAsync(
			RedisScripts.Acquire,
			[.. keys],
			[.. values]
		).WaitAsync(cancellationToken).ConfigureAwait(false);
		var ids = ((RedisResult[])result!)
			.Select(static value => (string)value!)
			.ToArray();
		return await ReadJobsAsync(ids, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask SetExecutionTelemetryAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var result = await EvaluateInt64Async(
			RedisScripts.SetTelemetry,
			[JobKey(jobId), ExecutionIndexKey(jobId), ExecutionDataKey(jobId)],
			[workerId, executionNumber, traceId ?? "", spanId ?? "", Ticks(startedAt)],
			cancellationToken
		).ConfigureAwait(false);
		ThrowIfNotOwned(result, jobId, workerId);
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var expiresAt = _timeProvider.GetUtcNow() + lease;
		var result = await EvaluateInt64Async(
			RedisScripts.RenewLease,
			[JobKey(jobId), LeasesKey],
			[workerId, executionNumber, Ticks(expiresAt), Score(expiresAt), jobId.JobId],
			cancellationToken
		).ConfigureAwait(false);
		ThrowIfNotOwned(result, jobId, workerId);
	}

	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var now = _timeProvider.GetUtcNow();
		var result = await EvaluateInt64Async(
			RedisScripts.Complete,
			[
				JobKey(jobId),
				LeasesKey,
				StateKey(JobState.Active),
				StateKey(JobState.Succeeded),
				CompletedKey(JobState.Succeeded),
				ExecutionIndexKey(jobId),
				ExecutionDataKey(jobId),
			],
			[workerId, executionNumber, Ticks(now), jobId.JobId, Score(now)],
			cancellationToken
		).ConfigureAwait(false);
		ThrowIfNotOwned(result, jobId, workerId);
	}

	/// <inheritdoc />
	public async ValueTask FailAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var now = _timeProvider.GetUtcNow();
		var nextTicks = nextRetryAt is { } retryAt ? Ticks(retryAt) : "";
		var nextScore = nextRetryAt is { } retryScore ? Score(retryScore) : 0;
		var result = await EvaluateInt64Async(
			RedisScripts.Fail,
			[
				JobKey(jobId),
				LeasesKey,
				StateKey(JobState.Active),
				StateKey(JobState.Failed),
				CompletedKey(JobState.Failed),
				StateKey(JobState.Scheduled),
				StateKey(JobState.Pending),
				ExecutionIndexKey(jobId),
				ExecutionDataKey(jobId),
			],
			[
				workerId,
				executionNumber,
				jobId.JobId,
				nextTicks,
				error,
				Ticks(now),
				Score(now),
				nextScore,
				Score(now),
				_root,
			],
			cancellationToken
		).ConfigureAwait(false);
		ThrowIfNotOwned(result, jobId, workerId);
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var states = Enum.GetValues<JobState>();
		var countTasks = states
			.Select(state => Database.SetLengthAsync(StateKey(state)))
			.ToArray();
		var recurringTask = ReadAllRecurringAsync(cancellationToken);
		var serversTask = ReadLiveServersAsync(cancellationToken);
		_ = await Task.WhenAll(countTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
		var recurring = await recurringTask.ConfigureAwait(false);
		var servers = await serversTask.ConfigureAwait(false);
		var counts = states
			.Select((state, index) => KeyValuePair.Create(state, countTasks[index].Result))
			.ToDictionary();
		return new JobMonitoringSnapshot
		{
			CapturedAt = _timeProvider.GetUtcNow(),
			Counts = counts,
			Recurring = recurring,
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
		cancellationToken.ThrowIfCancellationRequested();

		var take = Math.Min(query.Take, MaximumQueryTake);
		if (query.JobId is { } id)
		{
			var job = await ReadJobAsync(JobHandle.FromString(id), cancellationToken).ConfigureAwait(false);
			return job is not null && query.Skip == 0 && MatchesQuery(job, query) ? [job] : [];
		}

		if (!HasFilters(query))
		{
			var ids = await ReadJobIdsByRankAsync(query.Skip, take, cancellationToken).ConfigureAwait(false);
			return await ReadJobsAsync(ids, cancellationToken).ConfigureAwait(false);
		}

		var matchLimit = (long)query.Skip + take;
		var matches = new List<JobRecord>(take);
		long rank = 0;
		long matched = 0;
		while (matched < matchLimit)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var ids = await ReadJobIdsByRankAsync(rank, QueryWindowSize, cancellationToken).ConfigureAwait(false);
			if (ids.Count == 0)
				break;

			var jobs = await ReadJobsAsync(ids, cancellationToken).ConfigureAwait(false);
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
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobId,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var job = await ReadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
		if (job is null)
			return [];

		var synthetic = JobExecutionRecord.CreateSynthetic(job);
		var syntheticMissing = synthetic is not null
			&& (query.Attempt is null || query.Attempt == synthetic.Attempt)
			&& !await Database.HashExistsAsync(
				ExecutionDataKey(jobId),
				ExecutionField(synthetic.Attempt, "state")
			).WaitAsync(cancellationToken).ConfigureAwait(false);
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
				ExecutionDataKey(jobId),
				ExecutionField(attempt, "state")
			).WaitAsync(cancellationToken).ConfigureAwait(false);
			attempts = exists && skip == 0 ? [attempt] : [];
		}
		else
		{
			attempts = await Database.SortedSetRangeByRankAsync(
				ExecutionIndexKey(jobId),
				skip,
				skip + take - 1,
				Order.Descending
			).WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		result.AddRange(await ReadExecutionsAsync(jobId, attempts, cancellationToken).ConfigureAwait(false));
		return result;
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobId,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var job = await ReadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
		return job is null
			? null
			: new JobStatus
			{
				JobId = job.JobId,
				JobName = job.JobName,
				QueueName = job.QueueName,
				State = job.State,
				Attempt = job.Attempt,
				MaxAttempts = 0,
				CreatedAt = job.CreatedAt,
				DueAt = job.DueAt,
				CompletedAt = job.CompletedAt,
				LastError = job.LastError,
				BatchId = null,
				DependsOn = [],
			};
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var now = _timeProvider.GetUtcNow();
		var result = await EvaluateInt64Async(
			RedisScripts.Cancel,
			[JobKey(jobId), LeasesKey, ExecutionIndexKey(jobId), ExecutionDataKey(jobId)],
			[jobId.JobId, Ticks(now), Score(now), _root],
			cancellationToken
		).ConfigureAwait(false);
		if (result == 0)
			throw new KeyNotFoundException($"Job '{jobId}' was not found.");
		if (result < 0)
			throw new ImmediateJobException("Only a non-terminal job can be cancelled.");
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var now = _timeProvider.GetUtcNow();
		var result = await EvaluateInt64Async(
			RedisScripts.Retry,
			[
				JobKey(jobId),
				StateKey(JobState.Failed),
				StateKey(JobState.Scheduled),
				StateKey(JobState.Pending),
				CompletedKey(JobState.Failed),
				ExecutionIndexKey(jobId),
				ExecutionDataKey(jobId),
			],
			[Ticks(now), Score(now), jobId.JobId, _root],
			cancellationToken
		).ConfigureAwait(false);
		if (result == 0)
			throw new KeyNotFoundException($"Job '{jobId}' was not found.");
		if (result < 0)
			throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var result = await EvaluateInt64Async(
			RedisScripts.Delete,
			[JobKey(jobId), AllJobsKey, RecurringDedupeKey, ExecutionIndexKey(jobId), ExecutionDataKey(jobId)],
			[jobId.JobId, _root],
			cancellationToken
		).ConfigureAwait(false);
		if (result == 0)
			throw new KeyNotFoundException($"Job '{jobId}' was not found.");
		if (result < 0)
			throw new ImmediateJobException("Only terminal jobs can be deleted.");
	}

	/// <inheritdoc />
	public async ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var now = _timeProvider.GetUtcNow();
		await PurgeStateAsync(JobState.Succeeded, now - succeededRetention, cancellationToken).ConfigureAwait(false);
		await PurgeStateAsync(JobState.Failed, now - failedRetention, cancellationToken).ConfigureAwait(false);
		await PurgeStateAsync(JobState.Cancelled, now - failedRetention, cancellationToken).ConfigureAwait(false);
		await PurgeStateAsync(JobState.Skipped, now - failedRetention, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(
		JobServerSnapshot server,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		_ = await EvaluateInt64Async(
			RedisScripts.Heartbeat,
			[ServerKey(server.WorkerId), ServersKey],
			[
				Ticks(server.LastHeartbeat),
				server.ActiveWorkers,
				server.MaxWorkers,
				Score(server.LastHeartbeat),
				server.WorkerId,
				(long)TimeSpan.FromMinutes(2).TotalMilliseconds,
			],
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			await Database.PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (RedisException)
		{
			return false;
		}
	}

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(
		RecurringJobSchedule schedule,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var result = await EvaluateInt64Async(
			RedisScripts.UpsertRecurring,
			[RecurringKey(schedule.Name), RecurringNamesKey, RecurringDueKey],
			[
				JsonSerializer.Serialize(schedule, RedisJsonSerializerContext.Default.RecurringJobSchedule),
				schedule.IsCodeDefined ? 1 : 0,
				schedule.IsPaused ? 1 : 0,
				Ticks(schedule.NextRunAt),
				NullableTicks(schedule.LastRunAt),
				schedule.Name,
				Score(schedule.NextRunAt),
				RecurringDueMember(schedule.NextRunAt, schedule.Name),
			],
			cancellationToken
		).ConfigureAwait(false);
		if (result < 0)
			throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules.");
	}

	/// <inheritdoc />
	public async ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var values = new RedisValue[activeScheduleNames.Count + 1];
		values[0] = _root;
		var index = 1;
		foreach (var name in activeScheduleNames)
			values[index++] = name;
		_ = await EvaluateInt64Async(
			RedisScripts.RemoveObsoleteRecurring,
			[RecurringNamesKey, RecurringDueKey],
			values,
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var result = await EvaluateInt64Async(
			RedisScripts.RemoveRecurring,
			[RecurringKey(name), RecurringNamesKey, RecurringDueKey],
			[name],
			cancellationToken
		).ConfigureAwait(false);
		if (result == 0)
			throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
		if (result < 0)
			throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		await SetRecurringPausedAsync(name, isPaused: true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		await SetRecurringPausedAsync(name, isPaused: false, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var values = await Database.SortedSetRangeByScoreAsync(
			RecurringDueKey,
			stop: Score(now),
			take: batchSize
		).WaitAsync(cancellationToken).ConfigureAwait(false);
		var members = values.Select(static value => (string)value!).ToArray();
		var names = members.Select(static member => member[20..]).Distinct(StringComparer.Ordinal).ToArray();
		var schedules = await ReadRecurringAsync(
			names,
			cancellationToken
		).ConfigureAwait(false);
		var schedulesByName = schedules.ToDictionary(static schedule => schedule.Name, StringComparer.Ordinal);
		var due = new List<RecurringJobSchedule>(schedules.Count);
		var added = new HashSet<string>(StringComparer.Ordinal);
		var stale = new List<RedisValue>();
		foreach (var member in members)
		{
			var name = member[20..];
			if (!schedulesByName.TryGetValue(name, out var schedule)
				|| !string.Equals(member, RecurringDueMember(schedule.NextRunAt, name), StringComparison.Ordinal))
			{
				stale.Add(member);
				continue;
			}

			if (added.Add(name))
				due.Add(schedule);
		}

		if (stale.Count != 0)
		{
			_ = await Database.SortedSetRemoveAsync(RecurringDueKey, [.. stale])
				.WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		return due;
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		ValidateMaterializedJob(job);
		var jobArguments = CreateMaterializeArguments(schedule, job, nextRunAt, _timeProvider.GetUtcNow());
		var result = await EvaluateInt64Async(
			RedisScripts.MaterializeRecurring,
			[
				RecurringKey(schedule.Name),
				RecurringDedupeKey,
				JobKey(job.JobId),
				AllJobsKey,
				StateKey(job.State),
				DueKey(job.QueueName),
				RecurringDueKey,
				CompletedKey(job.State),
			],
			jobArguments,
			cancellationToken
		).ConfigureAwait(false);
		if (result < 0)
			throw new ImmediateJobException($"Job '{job.JobId}' already exists.");
		return result == 1;
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync() { }

	private async ValueTask SetRecurringPausedAsync(
		string name,
		bool isPaused,
		CancellationToken cancellationToken
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		var result = await EvaluateInt64Async(
			RedisScripts.SetRecurringPaused,
			[RecurringKey(name), RecurringDueKey],
			[isPaused ? 1 : 0],
			cancellationToken
		).ConfigureAwait(false);
		if (result == 0)
			throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
	}

	private async Task PurgeStateAsync(
		JobState state,
		DateTimeOffset cutoff,
		CancellationToken cancellationToken
	)
	{
		while (true)
		{
			var ids = await Database.SortedSetRangeByScoreAsync(
				CompletedKey(state),
				stop: Score(cutoff),
				exclude: Exclude.Stop,
				take: 256
			).WaitAsync(cancellationToken).ConfigureAwait(false);
			if (ids.Length == 0)
				return;
			foreach (var value in ids)
			{
				var id = JobHandle.FromString((string)value!);

				_ = await EvaluateInt64Async(
					RedisScripts.Purge,
					[
						JobKey(id),
						CompletedKey(state),
						AllJobsKey,
						StateKey(state),
						RecurringDedupeKey,
						ExecutionIndexKey(id),
						ExecutionDataKey(id),
					],
					[value, (int)state],
					cancellationToken
				).ConfigureAwait(false);
			}
		}
	}

	private async Task<IReadOnlyList<JobServerSnapshot>> ReadLiveServersAsync(CancellationToken cancellationToken)
	{
		var cutoff = _timeProvider.GetUtcNow() - TimeSpan.FromMinutes(2);
		var stale = await Database.SortedSetRangeByScoreAsync(
			ServersKey,
			stop: Score(cutoff),
			exclude: Exclude.Stop
		).WaitAsync(cancellationToken).ConfigureAwait(false);
		if (stale.Length != 0)
			_ = await Database.SortedSetRemoveAsync(ServersKey, stale).WaitAsync(cancellationToken).ConfigureAwait(false);
		var ids = await Database.SortedSetRangeByScoreAsync(
			ServersKey,
			start: Score(cutoff)
		).WaitAsync(cancellationToken).ConfigureAwait(false);
		var tasks = ids
			.Select(id => Database.HashGetAsync(ServerKey((string)id!), ["last", "active", "max"]))
			.ToArray();
		_ = await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
		return
		[
			.. tasks
				.Select((task, index) => (Id: (string)ids[index]!, Values: task.Result))
				.Where(static server => !server.Values[0].IsNullOrEmpty)
				.Select(static server => new JobServerSnapshot
				{
					WorkerId = server.Id,
					LastHeartbeat = FromTicks(server.Values[0]),
					ActiveWorkers = ParseInt32(server.Values[1]),
					MaxWorkers = ParseInt32(server.Values[2]),
				}),
		];
	}

	private async Task<IReadOnlyList<RecurringJobSchedule>> ReadAllRecurringAsync(CancellationToken cancellationToken)
	{
		var names = await Database.SetMembersAsync(RecurringNamesKey)
			.WaitAsync(cancellationToken)
			.ConfigureAwait(false);
		return await ReadRecurringAsync(
			[.. names.Select(static value => (string)value!)],
			cancellationToken
		).ConfigureAwait(false);
	}

	private async Task<IReadOnlyList<RecurringJobSchedule>> ReadRecurringAsync(
		IReadOnlyList<string> names,
		CancellationToken cancellationToken
	)
	{
		var tasks = names.Select(name => ReadRecurringAsync(name, cancellationToken).AsTask()).ToArray();
		var schedules = await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
		return [.. schedules.OfType<RecurringJobSchedule>().OrderBy(schedule => schedule.NextRunAt).ThenBy(schedule => schedule.Name, StringComparer.Ordinal)];
	}

	private async ValueTask<RecurringJobSchedule?> ReadRecurringAsync(
		string name,
		CancellationToken cancellationToken
	)
	{
		var values = await Database.HashGetAsync(RecurringKey(name), RecurringMutableFields)
			.WaitAsync(cancellationToken)
			.ConfigureAwait(false);
		if (values[0].IsNull)
			return null;
		var schedule = JsonSerializer.Deserialize(
			(string)values[0]!,
			RedisJsonSerializerContext.Default.RecurringJobSchedule
		) ?? throw new ImmediateJobException($"Recurring schedule '{name}' contains invalid data.");
		return schedule with
		{
			IsPaused = values[1] == "1",
			NextRunAt = FromTicks(values[2]),
			LastRunAt = FromNullableTicks(values[3]),
		};
	}

	private async Task<IReadOnlyList<JobRecord>> ReadJobsAsync(
		IReadOnlyList<string> ids,
		CancellationToken cancellationToken
	)
	{
		var tasks = ids.Select(id => ReadJobAsync(new() { JobId = id }, cancellationToken).AsTask()).ToArray();
		var jobs = await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
		return [.. jobs.OfType<JobRecord>()];
	}

	private async Task<IReadOnlyList<JobExecutionRecord>> ReadExecutionsAsync(
		JobHandle jobId,
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

		var allValues = await Database.HashGetAsync(ExecutionDataKey(jobId), fields)
			.WaitAsync(cancellationToken)
			.ConfigureAwait(false);

		var executions = new List<JobExecutionRecord>(attempts.Length);
		for (var index = 0; index < attempts.Length; index++)
		{
			var values = allValues.AsSpan(index * ExecutionFieldNames.Length, ExecutionFieldNames.Length);
			if (values[0].IsNull)
				continue;
			executions.Add(new()
			{
				JobId = jobId,
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

	private async Task<IReadOnlyList<string>> ReadJobIdsByRankAsync(
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
		).WaitAsync(cancellationToken).ConfigureAwait(false);
		return [.. values.Select(static value => (string)value!)];
	}

	private static bool HasFilters(JobQuery query) =>
		query.State is not null ||
		!string.IsNullOrWhiteSpace(query.QueueName) ||
		!string.IsNullOrWhiteSpace(query.JobName) ||
		!string.IsNullOrWhiteSpace(query.Search);

	private static bool MatchesQuery(JobRecord job, JobQuery query) =>
		(query.State is not { } state || job.State == state) &&
		(string.IsNullOrWhiteSpace(query.QueueName) || string.Equals(job.QueueName, query.QueueName, StringComparison.Ordinal)) &&
		(string.IsNullOrWhiteSpace(query.JobName) || string.Equals(job.JobName, query.JobName, StringComparison.Ordinal)) &&
		(string.IsNullOrWhiteSpace(query.Search) ||
			job.JobName.Contains(query.Search, StringComparison.OrdinalIgnoreCase));

	private async ValueTask<JobRecord?> ReadJobAsync(JobHandle id, CancellationToken cancellationToken)
	{
		var values = await Database.HashGetAsync(JobKey(id), JobMutableFields)
			.WaitAsync(cancellationToken)
			.ConfigureAwait(false);
		if (values[0].IsNull)
			return null;
		var job = JsonSerializer.Deserialize(
			(string)values[0]!,
			RedisJsonSerializerContext.Default.JobRecord
		) ?? throw new ImmediateJobException($"Job '{id}' contains invalid data.");
		return job with
		{
			State = (JobState)ParseInt32(values[1]),
			DueAt = FromTicks(values[2]),
			Attempt = ParseInt32(values[3]),
			WorkerId = NullIfEmpty(values[4]),
			LeaseExpiresAt = FromNullableTicks(values[5]),
			LastError = NullIfEmpty(values[6]),
			CompletedAt = FromNullableTicks(values[7]),
			ExecutionTraceId = NullIfEmpty(values[8]),
			ExecutionSpanId = NullIfEmpty(values[9]),
			ExecutionStartedAt = FromNullableTicks(values[10]),
		};
	}

	private async ValueTask<long> EvaluateInt64Async(
		string script,
		RedisKey[] keys,
		RedisValue[] values,
		CancellationToken cancellationToken
	)
	{
		var result = await Database.ScriptEvaluateAsync(script, keys, values)
			.WaitAsync(cancellationToken)
			.ConfigureAwait(false);
		return (long)result;
	}

	private static RedisValue[] CreateEnqueueArguments(JobRecord job) =>
	[
		JsonSerializer.Serialize(job, RedisJsonSerializerContext.Default.JobRecord),
		(int)job.State,
		Ticks(job.DueAt),
		job.Attempt,
		job.WorkerId ?? "",
		NullableTicks(job.LeaseExpiresAt),
		job.LastError ?? "",
		NullableTicks(job.CompletedAt),
		job.ExecutionTraceId ?? "",
		job.ExecutionSpanId ?? "",
		NullableTicks(job.ExecutionStartedAt),
		job.QueueName,
		job.JobName,
		Score(job.CreatedAt),
		job.JobId.JobId,
		Score(job.DueAt),
		Ticks(job.CreatedAt),
		DueMember(job),
	];

	private static RedisValue[] CreateMaterializeArguments(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		DateTimeOffset now
	) =>
	[
		Ticks(schedule.NextRunAt),
		job.RecurringKey ?? "",
		job.JobId.JobId,
		JsonSerializer.Serialize(job, RedisJsonSerializerContext.Default.JobRecord),
		(int)job.State,
		Ticks(job.DueAt),
		Score(job.DueAt),
		job.Attempt,
		job.WorkerId ?? "",
		NullableTicks(job.LeaseExpiresAt),
		job.LastError ?? "",
		NullableTicks(job.CompletedAt),
		job.ExecutionTraceId ?? "",
		job.ExecutionSpanId ?? "",
		NullableTicks(job.ExecutionStartedAt),
		job.QueueName,
		job.JobName,
		Score(job.CreatedAt),
		Ticks(nextRunAt),
		Score(nextRunAt),
		schedule.Name,
		Ticks(job.CreatedAt),
		job.CompletedAt is { } completedAt ? Score(completedAt) : 0,
		Score(now),
	];

	private static void ValidateQueueJob(JobRecord job)
	{
		if (job.BatchId is not null || job.RemainingDependencies != 0 || job.FailedDependencies != 0)
		{
			throw new NotSupportedException(
				"Batches & continuations require a graph-capable storage provider (a SQL database)."
			);
		}

		if (job.State is not (JobState.Pending or JobState.Scheduled))
			throw new ImmediateJobException($"Queue job '{job.JobId}' has invalid state '{job.State}'.");
	}

	private static void ValidateMaterializedJob(JobRecord job)
	{
		if (job.BatchId is not null || job.RemainingDependencies != 0 || job.FailedDependencies != 0)
		{
			throw new NotSupportedException(
				"Batches & continuations require a graph-capable storage provider (a SQL database)."
			);
		}

		if (job.State is not (JobState.Pending or JobState.Scheduled or JobState.Cancelled or JobState.Skipped))
			throw new ImmediateJobException($"Recurring job '{job.JobId}' has invalid state '{job.State}'.");
		if (job.CompletedAt is null && (job.State == JobState.Cancelled || job.State == JobState.Skipped))
			throw new ImmediateJobException($"Terminal recurring job '{job.JobId}' must have a completion time.");
	}

	private static void ThrowIfNotOwned(long result, JobHandle jobId, string workerId)
	{
		if (result <= 0)
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobId}'.");
	}

	private RedisKey JobKey(JobHandle id) => _root + "job:" + id.JobId;
	private RedisKey ExecutionIndexKey(JobHandle id) => _root + "executions:index:" + id.JobId;
	private RedisKey ExecutionDataKey(JobHandle id) => _root + "executions:data:" + id.JobId;
	private RedisKey DueKey(string queue) => _root + "due:" + queue;
	private RedisKey StateKey(JobState state) => string.Create(CultureInfo.InvariantCulture, $"{_root}state:{(int)state}");
	private RedisKey CompletedKey(JobState state) => string.Create(CultureInfo.InvariantCulture, $"{_root}completed:{(int)state}");
	private RedisKey RecurringKey(string name) => _root + "recurring:" + name;
	private RedisKey ServerKey(string workerId) => _root + "server:" + workerId;
	private RedisKey AllJobsKey => _root + "jobs";
	private RedisKey LeasesKey => _root + "leases";
	private RedisKey RecurringNamesKey => _root + "recurring:names";
	private RedisKey RecurringDueKey => _root + "recurring:due";
	private RedisKey RecurringDedupeKey => _root + "recurring:dedupe";
	private RedisKey ServersKey => _root + "servers";

	private static long Score(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
	private static string Ticks(DateTimeOffset value) => value.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);
	private static string DueMember(JobRecord job) => $"{Ticks(job.DueAt)}|{Ticks(job.CreatedAt)}|{job.JobId}";
	private static string RecurringDueMember(DateTimeOffset nextRunAt, string name) => $"{Ticks(nextRunAt)}|{name}";
	private static string NullableTicks(DateTimeOffset? value) => value is { } actual ? Ticks(actual) : "";
	private static RedisValue ExecutionField(int executionNumber, string name) =>
		string.Create(CultureInfo.InvariantCulture, $"{executionNumber}:{name}");
	private static DateTimeOffset FromTicks(RedisValue value) =>
		new(long.Parse((string)value!, NumberStyles.None, CultureInfo.InvariantCulture), TimeSpan.Zero);
	private static DateTimeOffset? FromNullableTicks(RedisValue value) =>
		value.IsNullOrEmpty ? null : FromTicks(value);
	private static int ParseInt32(RedisValue value) =>
		int.Parse((string)value!, NumberStyles.Integer, CultureInfo.InvariantCulture);
	private static string? NullIfEmpty(RedisValue value) => value.IsNullOrEmpty ? null : (string)value!;
}

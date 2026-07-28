using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

/// <summary>
/// Distributed Redis storage for ordinary queue jobs and recurring schedules.
/// Batches and continuations require a graph-capable SQL provider.
/// </summary>
public sealed class RedisJobStorage : IRecurringJobStorage, IDisposable
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

	private readonly IConnectionMultiplexer _connection;
	private readonly IDatabase _database;
	private readonly TimeProvider _timeProvider;
	private readonly string _root;
	private readonly bool _ownsConnection;
#pragma warning disable IDE0330 // System.Threading.Lock is unavailable on the lowest target framework.
	private readonly object _disposeGate = new();
#pragma warning restore IDE0330
	private Task? _disposeTask;

	/// <summary>Creates storage over an existing Redis connection.</summary>
	public RedisJobStorage(
		IConnectionMultiplexer connection,
		RedisJobStorageOptions? options = null,
		TimeProvider? timeProvider = null
	) : this(connection, options ?? new(), timeProvider, ownsConnection: false)
	{
	}

	internal RedisJobStorage(
		IConnectionMultiplexer connection,
		RedisJobStorageOptions options,
		TimeProvider? timeProvider,
		bool ownsConnection
	)
	{
		ArgumentNullException.ThrowIfNull(connection);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);
		if (options.KeyPrefix.IndexOfAny(['{', '}']) >= 0)
			throw new ArgumentException("The Redis key prefix cannot contain '{' or '}'.", nameof(options));

		_connection = connection;
		_database = connection.GetDatabase(options.Database);
		_timeProvider = timeProvider ?? TimeProvider.System;
		_root = $"{{{options.KeyPrefix}}}:";
		_ownsConnection = ownsConnection;
	}

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		_ = await _database.PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		ValidateQueueJob(job);
		var result = await EvaluateInt64Async(
			RedisScripts.Enqueue,
			[JobKey(job.Id), AllJobsKey, StateKey(job.State), DueKey(job.QueueName)],
			CreateEnqueueArguments(job),
			cancellationToken
		).ConfigureAwait(false);
		if (result == 0)
			throw new ImmediateJobException($"Job '{job.Id}' already exists.");
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Lease, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.BatchSize, 0);
		ArgumentNullException.ThrowIfNull(request.Queues);
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
		};
		foreach (var queue in request.Queues)
		{
			ArgumentNullException.ThrowIfNull(queue);
			ArgumentException.ThrowIfNullOrWhiteSpace(queue.QueueName);
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

		var result = await _database.ScriptEvaluateAsync(
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
		string jobId,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		var result = await EvaluateInt64Async(
			RedisScripts.SetTelemetry,
			[JobKey(jobId)],
			[workerId, traceId ?? "", spanId ?? "", Ticks(startedAt)],
			cancellationToken
		).ConfigureAwait(false);
		ThrowIfNotOwned(result, jobId, workerId);
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		string jobId,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
		var expiresAt = _timeProvider.GetUtcNow() + lease;
		var result = await EvaluateInt64Async(
			RedisScripts.RenewLease,
			[JobKey(jobId), LeasesKey],
			[workerId, Ticks(expiresAt), Score(expiresAt), jobId],
			cancellationToken
		).ConfigureAwait(false);
		ThrowIfNotOwned(result, jobId, workerId);
	}

	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		string jobId,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		var now = _timeProvider.GetUtcNow();
		var result = await EvaluateInt64Async(
			RedisScripts.Complete,
			[
				JobKey(jobId),
				LeasesKey,
				StateKey(JobState.Active),
				StateKey(JobState.Succeeded),
				CompletedKey(JobState.Succeeded),
			],
			[workerId, Ticks(now), jobId, Score(now)],
			cancellationToken
		).ConfigureAwait(false);
		ThrowIfNotOwned(result, jobId, workerId);
	}

	/// <inheritdoc />
	public async ValueTask FailAsync(
		string jobId,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentNullException.ThrowIfNull(error);
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
			],
			[
				workerId,
				jobId,
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
		var states = Enum.GetValues<JobState>();
		var countTasks = states
			.Select(state => _database.SetLengthAsync(StateKey(state)))
			.ToArray();
		var recurringTask = ReadAllRecurringAsync(cancellationToken);
		var serversTask = ReadLiveServersAsync(cancellationToken);
		_ = await Task.WhenAll(countTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
		var recurring = await recurringTask.ConfigureAwait(false);
		var servers = await serversTask.ConfigureAwait(false);
		var counts = states
			.Select((state, index) => KeyValuePair.Create(state, countTasks[index].Result))
			.ToDictionary();
		return new(_timeProvider.GetUtcNow(), counts, recurring, servers)
		{
			Capabilities = this.GetCapabilities(),
		};
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0);
		var take = Math.Min(query.Take, MaximumQueryTake);
		if (query.Id is { } id)
		{
			var job = await ReadJobAsync(id, cancellationToken).ConfigureAwait(false);
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
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		string jobId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		var job = await ReadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
		return job is null
			? null
			: new(
				job.Id,
				job.JobName,
				job.QueueName,
				job.State,
				job.Attempt,
				MaxAttempts: null,
				job.CreatedAt,
				job.DueAt,
				job.CompletedAt,
				job.LastError,
				BatchId: null,
				DependsOn: []
			);
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		var now = _timeProvider.GetUtcNow();
		var result = await EvaluateInt64Async(
			RedisScripts.Retry,
			[
				JobKey(jobId),
				StateKey(JobState.Failed),
				StateKey(JobState.Pending),
				CompletedKey(JobState.Failed),
			],
			[Ticks(now), Score(now), jobId, _root],
			cancellationToken
		).ConfigureAwait(false);
		if (result == 0)
			throw new KeyNotFoundException($"Job '{jobId}' was not found.");
		if (result < 0)
			throw new ImmediateJobException("Only failed jobs can be retried.");
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		var result = await EvaluateInt64Async(
			RedisScripts.Delete,
			[JobKey(jobId), AllJobsKey],
			[jobId, _root],
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
		ArgumentOutOfRangeException.ThrowIfLessThan(succeededRetention, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThan(failedRetention, TimeSpan.Zero);
		var now = _timeProvider.GetUtcNow();
		await PurgeStateAsync(JobState.Succeeded, now - succeededRetention, cancellationToken).ConfigureAwait(false);
		await PurgeStateAsync(JobState.Failed, now - failedRetention, cancellationToken).ConfigureAwait(false);
		await PurgeStateAsync(JobState.Cancelled, now - failedRetention, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(
		JobServerSnapshot server,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(server);
		ArgumentException.ThrowIfNullOrWhiteSpace(server.WorkerId);
		_ = await EvaluateInt64Async(
			RedisScripts.Heartbeat,
			[ServerKey(server.WorkerId), ServersKey],
			[
				Ticks(server.LastHeartbeat),
				server.ActiveWorkers,
				server.MaxWorkers,
				Score(server.LastHeartbeat),
				server.WorkerId,
			],
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			_ = await _database.PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
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
		ValidateRecurring(schedule);
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
		ArgumentNullException.ThrowIfNull(activeScheduleNames);
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
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
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
	public ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default) =>
		SetRecurringPausedAsync(name, isPaused: true, cancellationToken);

	/// <inheritdoc />
	public ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default) =>
		SetRecurringPausedAsync(name, isPaused: false, cancellationToken);

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
		var values = await _database.SortedSetRangeByScoreAsync(
			RecurringDueKey,
			stop: Score(now),
			take: batchSize
		).WaitAsync(cancellationToken).ConfigureAwait(false);
		return await ReadRecurringAsync(
			[.. values.Select(static value => ((string)value!)[20..])],
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	)
	{
		ValidateRecurring(schedule);
		ValidateMaterializedJob(job);
		var jobArguments = CreateMaterializeArguments(schedule, job, nextRunAt);
		var result = await EvaluateInt64Async(
			RedisScripts.MaterializeRecurring,
			[
				RecurringKey(schedule.Name),
				RecurringDedupeKey,
				JobKey(job.Id),
				AllJobsKey,
				StateKey(job.State),
				DueKey(job.QueueName),
				RecurringDueKey,
				CompletedKey(JobState.Cancelled),
			],
			jobArguments,
			cancellationToken
		).ConfigureAwait(false);
		if (result < 0)
			throw new ImmediateJobException($"Job '{job.Id}' already exists.");
		return result == 1;
	}

	/// <inheritdoc />
	public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

	/// <inheritdoc />
	public ValueTask DisposeAsync()
	{
		lock (_disposeGate)
			return new(_disposeTask ??= DisposeCoreAsync());
	}

	private async Task DisposeCoreAsync()
	{
		if (_ownsConnection)
		{
			try
			{
				await _connection.CloseAsync().ConfigureAwait(false);
			}
			finally
			{
				_connection.Dispose();
			}
		}
	}

	private async ValueTask SetRecurringPausedAsync(
		string name,
		bool isPaused,
		CancellationToken cancellationToken
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		var schedule = await ReadRecurringAsync(name, cancellationToken).ConfigureAwait(false)
			?? throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
		var result = await EvaluateInt64Async(
			RedisScripts.SetRecurringPaused,
			[RecurringKey(name), RecurringDueKey],
			[
				isPaused ? 1 : 0,
				name,
				Score(schedule.NextRunAt),
				RecurringDueMember(schedule.NextRunAt, name),
			],
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
			var ids = await _database.SortedSetRangeByScoreAsync(
				CompletedKey(state),
				stop: Score(cutoff),
				exclude: Exclude.Stop,
				take: 256
			).WaitAsync(cancellationToken).ConfigureAwait(false);
			if (ids.Length == 0)
				return;
			foreach (var value in ids)
			{
				var id = (string)value!;
				_ = await EvaluateInt64Async(
					RedisScripts.Purge,
					[JobKey(id), CompletedKey(state), AllJobsKey, StateKey(state)],
					[id, (int)state],
					cancellationToken
				).ConfigureAwait(false);
			}
		}
	}

	private async Task<IReadOnlyList<JobServerSnapshot>> ReadLiveServersAsync(CancellationToken cancellationToken)
	{
		var cutoff = _timeProvider.GetUtcNow() - TimeSpan.FromMinutes(2);
		var stale = await _database.SortedSetRangeByScoreAsync(
			ServersKey,
			stop: Score(cutoff),
			exclude: Exclude.Stop
		).WaitAsync(cancellationToken).ConfigureAwait(false);
		if (stale.Length != 0)
			_ = await _database.SortedSetRemoveAsync(ServersKey, stale).WaitAsync(cancellationToken).ConfigureAwait(false);
		var ids = await _database.SortedSetRangeByScoreAsync(
			ServersKey,
			start: Score(cutoff)
		).WaitAsync(cancellationToken).ConfigureAwait(false);
		var tasks = ids
			.Select(id => _database.HashGetAsync(ServerKey((string)id!), ["last", "active", "max"]))
			.ToArray();
		_ = await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
		return
		[
			.. tasks.Select((task, index) => new JobServerSnapshot(
				(string)ids[index]!,
				FromTicks(task.Result[0]),
				ParseInt32(task.Result[1]),
				ParseInt32(task.Result[2])
			)),
		];
	}

	private async Task<IReadOnlyList<RecurringJobSchedule>> ReadAllRecurringAsync(CancellationToken cancellationToken)
	{
		var names = await _database.SetMembersAsync(RecurringNamesKey)
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
		return [.. schedules.OfType<RecurringJobSchedule>().OrderBy(schedule => schedule.NextRunAt).ThenBy(schedule => schedule.Name)];
	}

	private async ValueTask<RecurringJobSchedule?> ReadRecurringAsync(
		string name,
		CancellationToken cancellationToken
	)
	{
		var values = await _database.HashGetAsync(RecurringKey(name), RecurringMutableFields)
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
		var tasks = ids.Select(id => ReadJobAsync(id, cancellationToken).AsTask()).ToArray();
		var jobs = await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
		return [.. jobs.OfType<JobRecord>()];
	}

	private async Task<IReadOnlyList<string>> ReadJobIdsByRankAsync(
		long start,
		int count,
		CancellationToken cancellationToken
	)
	{
		var values = await _database.SortedSetRangeByRankAsync(
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
		!string.IsNullOrWhiteSpace(query.Search);

	private static bool MatchesQuery(JobRecord job, JobQuery query) =>
		(query.State is not { } state || job.State == state) &&
		(string.IsNullOrWhiteSpace(query.QueueName) || job.QueueName == query.QueueName) &&
		(string.IsNullOrWhiteSpace(query.Search) ||
			job.JobName.Contains(query.Search, StringComparison.OrdinalIgnoreCase));

	private async ValueTask<JobRecord?> ReadJobAsync(string id, CancellationToken cancellationToken)
	{
		var values = await _database.HashGetAsync(JobKey(id), JobMutableFields)
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
		var result = await _database.ScriptEvaluateAsync(script, keys, values)
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
		job.Id,
		Score(job.DueAt),
		Ticks(job.CreatedAt),
		DueMember(job),
	];

	private static RedisValue[] CreateMaterializeArguments(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt
	) =>
	[
		Ticks(schedule.NextRunAt),
		job.RecurringKey ?? "",
		job.Id,
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
	];

	private static void ValidateQueueJob(JobRecord job)
	{
		ArgumentNullException.ThrowIfNull(job);
		ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);
		ArgumentException.ThrowIfNullOrWhiteSpace(job.JobName);
		ArgumentException.ThrowIfNullOrWhiteSpace(job.QueueName);
		if (job.BatchId is not null || job.RemainingDependencies != 0 || job.FailedDependencies != 0)
		{
			throw new NotSupportedException(
				"Batches & continuations require a graph-capable storage provider (a SQL database)."
			);
		}

		if (job.State is not (JobState.Pending or JobState.Scheduled))
			throw new ImmediateJobException($"Queue job '{job.Id}' has invalid state '{job.State}'.");
	}

	private static void ValidateMaterializedJob(JobRecord job)
	{
		ArgumentNullException.ThrowIfNull(job);
		ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);
		ArgumentException.ThrowIfNullOrWhiteSpace(job.JobName);
		ArgumentException.ThrowIfNullOrWhiteSpace(job.QueueName);
		if (job.BatchId is not null || job.RemainingDependencies != 0 || job.FailedDependencies != 0)
		{
			throw new NotSupportedException(
				"Batches & continuations require a graph-capable storage provider (a SQL database)."
			);
		}

		if (job.State is not (JobState.Pending or JobState.Scheduled or JobState.Cancelled))
			throw new ImmediateJobException($"Recurring job '{job.Id}' has invalid state '{job.State}'.");
		if (job.State == JobState.Cancelled && job.CompletedAt is null)
			throw new ImmediateJobException($"Cancelled recurring job '{job.Id}' must have a completion time.");
	}

	private static void ValidateRecurring(RecurringJobSchedule schedule)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		ArgumentException.ThrowIfNullOrWhiteSpace(schedule.Name);
		ArgumentException.ThrowIfNullOrWhiteSpace(schedule.JobName);
		ArgumentException.ThrowIfNullOrWhiteSpace(schedule.Cron);
		ArgumentException.ThrowIfNullOrWhiteSpace(schedule.TimeZone);
	}

	private static void ThrowIfNotOwned(long result, string jobId, string workerId)
	{
		if (result <= 0)
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobId}'.");
	}

	private RedisKey JobKey(string id) => _root + "job:" + id;
	private RedisKey DueKey(string queue) => _root + "due:" + queue;
	private RedisKey StateKey(JobState state) => _root + "state:" + (int)state;
	private RedisKey CompletedKey(JobState state) => _root + "completed:" + (int)state;
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
	private static string DueMember(JobRecord job) => $"{Ticks(job.DueAt)}|{Ticks(job.CreatedAt)}|{job.Id}";
	private static string RecurringDueMember(DateTimeOffset nextRunAt, string name) => $"{Ticks(nextRunAt)}|{name}";
	private static string NullableTicks(DateTimeOffset? value) => value is { } actual ? Ticks(actual) : "";
	private static DateTimeOffset FromTicks(RedisValue value) =>
		new(long.Parse((string)value!, NumberStyles.None, CultureInfo.InvariantCulture), TimeSpan.Zero);
	private static DateTimeOffset? FromNullableTicks(RedisValue value) =>
		value.IsNullOrEmpty ? null : FromTicks(value);
	private static int ParseInt32(RedisValue value) =>
		int.Parse((string)value!, NumberStyles.Integer, CultureInfo.InvariantCulture);
	private static string? NullIfEmpty(RedisValue value) => value.IsNullOrEmpty ? null : (string)value!;
}

using System.Globalization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// A best-effort, non-durable, single-node provider intended for development and tests.
/// 
/// </summary>
/// <param name="timeProvider">
/// 	The clock used for scheduling, leases, and timestamps.
/// </param>
internal sealed class InMemoryJobStorage(TimeProvider timeProvider) :
	IRecurringJobStorage,
	IJobGraphStorage,
	IFairQueueStorage
{
	private readonly Lock _gate = new();
	private readonly Dictionary<JobHandle, JobRecord> _jobs = [];
	private readonly Dictionary<JobHandle, SortedDictionary<int, JobExecutionRecord>> _executions = [];
	private readonly Dictionary<BatchHandle, BatchRecord> _batches = [];
	private readonly List<JobContinuationEdge> _edges = [];
	private readonly HashSet<JobContinuationEdge> _settledEdges = [];
	private readonly Dictionary<string, RecurringJobSchedule> _recurring = [with(StringComparer.Ordinal)];
	private readonly Dictionary<string, JobServerSnapshot> _servers = [with(StringComparer.Ordinal)];
	private readonly HashSet<string> _recurringKeys = [with(StringComparer.Ordinal)];
	private readonly Dictionary<(string QueueName, string GroupId), long> _fairQueueLastServed = [];

	/// <inheritdoc />
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;

	/// <inheritdoc />
	public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask EnqueueAsync(JobRecord jobRecord, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_jobs.TryAdd(jobRecord.JobId, jobRecord))
				throw new ImmediateJobException($"Job '{jobRecord.JobId}' already exists.");
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			ValidateNewJob(job);
			ValidateEdges([job], edges, batchId: null);

			var restoreExistingState = HasTerminalParent(edges) &&
				(job.State != JobState.AwaitingContinuation || job.RemainingDependencies < edges.Count);
			_jobs.Add(job.JobId, restoreExistingState ? job : NormalizeWaitingJob(job, edges.Count));
			_edges.AddRange(edges);
			if (restoreExistingState)
				MarkTerminalParentEdgesSettled(edges);
			else
				EvaluateAlreadyTerminalParents(edges);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			ValidateBatch(batch, jobs, edges);

			var restoreExistingState = IsRecoveredBatch(batch, jobs, edges);
			_batches.Add(batch.BatchId, batch);

			var incomingCounts = edges
				.GroupBy(static edge => edge.ChildJobId)
				.ToDictionary(static group => group.Key, static group => group.Count());

			foreach (var job in jobs)
			{
				_jobs.Add(
					job.JobId,
					restoreExistingState
						? job
						: incomingCounts.TryGetValue(job.JobId, out var dependencyCount)
						? NormalizeWaitingJob(job, dependencyCount)
						: job with { BatchId = batch.BatchId, RemainingDependencies = 0, FailedDependencies = 0 }
				);
			}

			_edges.AddRange(edges);
			if (restoreExistingState)
				MarkTerminalParentEdgesSettled(edges);
			else
				EvaluateAlreadyTerminalParents(edges);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			foreach (var expired in _jobs.Values.Where(x => x.State == JobState.Active && x.LeaseExpiresAt <= now).ToArray())
			{
				InterruptExecution(expired);
				_jobs[expired.JobId] = expired with
				{
					State = JobState.Pending,
					WorkerId = null,
					LeaseExpiresAt = null,
				};
			}

			if (request.FairQueues is null)
				return AcquireInExistingOrder(request, now);

			var acquired = new List<JobRecord>(request.BatchSize);
			foreach (var queue in request.Queues)
			{
				var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
				if (queueCapacity <= 0)
					continue;

				var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
				if (!HasEligibleGroupedJob(queue.QueueName, jobCapacities, now))
				{
					AcquireQueueInExistingOrder(
						request,
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						now,
						acquired
					);
					continue;
				}

				AcquireQueueFairly(
					request,
					queue.QueueName,
					jobCapacities,
					queueCapacity,
					now,
					acquired
				);
			}

			return acquired;
		}
	}

	private List<JobRecord> AcquireInExistingOrder(JobAcquisitionRequest request, DateTimeOffset now)
	{
		var acquired = new List<JobRecord>(request.BatchSize);
		foreach (var queue in request.Queues)
		{
			var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
			if (queueCapacity <= 0)
				continue;

			var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
			AcquireQueueInExistingOrder(
				request,
				queue.QueueName,
				jobCapacities,
				queueCapacity,
				now,
				acquired
			);
		}

		return acquired;
	}

	private void AcquireQueueInExistingOrder(
		JobAcquisitionRequest request,
		string queueName,
		Dictionary<string, int> jobCapacities,
		int queueCapacity,
		DateTimeOffset now,
		List<JobRecord> acquired
	)
	{
		foreach (var candidate in _jobs.Values
			.Where(job => string.Equals(job.QueueName, queueName, StringComparison.Ordinal) &&
				jobCapacities.ContainsKey(job.JobName) &&
				job.State is JobState.Pending or JobState.Scheduled &&
				job.DueAt <= now)
			.OrderBy(job => job.DueAt)
			.ThenBy(job => job.CreatedAt)
			.ThenBy(job => job.JobId.JobId, StringComparer.Ordinal))
		{
			if (queueCapacity == 0)
				break;
			if (jobCapacities[candidate.JobName] <= 0)
				continue;

			acquired.Add(Acquire(candidate, request, now));
			jobCapacities[candidate.JobName]--;
			queueCapacity--;
		}
	}

	private bool HasEligibleGroupedJob(
		string queueName,
		Dictionary<string, int> jobCapacities,
		DateTimeOffset now
	) =>
		_jobs.Values.Any(job => string.Equals(job.QueueName, queueName, StringComparison.Ordinal) &&
			job.GroupId is not null &&
			jobCapacities.TryGetValue(job.JobName, out var capacity) &&
			capacity > 0 &&
			job.State is JobState.Pending or JobState.Scheduled &&
			job.DueAt <= now);

	private void AcquireQueueFairly(
		JobAcquisitionRequest request,
		string queueName,
		Dictionary<string, int> jobCapacities,
		int queueCapacity,
		DateTimeOffset now,
		List<JobRecord> acquired
	)
	{
		var policy = request.FairQueues!;
		while (queueCapacity > 0)
		{
			var eligible = _jobs.Values
				.Where(job => string.Equals(job.QueueName, queueName, StringComparison.Ordinal) &&
					jobCapacities.TryGetValue(job.JobName, out var capacity) &&
					capacity > 0 &&
					job.State is JobState.Pending or JobState.Scheduled &&
					job.DueAt <= now)
				.ToArray();
			if (eligible.Length == 0)
				break;

			var activeCounts = _jobs.Values
				.Where(job => string.Equals(job.QueueName, queueName, StringComparison.Ordinal) &&
					job.GroupId is not null &&
					job.State == JobState.Active &&
					job.LeaseExpiresAt > now)
				.GroupBy(static job => job.GroupId!, StringComparer.Ordinal)
				.ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
			var totalActive = _jobs.Values.Count(job => string.Equals(job.QueueName, queueName, StringComparison.Ordinal) &&
				job.State == JobState.Active &&
				job.LeaseExpiresAt > now);
			var groupedHeads = eligible
				.Where(static job => job.GroupId is not null)
				.GroupBy(static job => job.GroupId!, StringComparer.Ordinal)
				.Select(static group => group
					.OrderBy(static job => job.DueAt)
					.ThenBy(static job => job.CreatedAt)
					.ThenBy(static job => job.JobId)
					.First());
			var ungroupedHead = eligible
				.Where(static job => job.GroupId is null)
				.OrderBy(static job => job.DueAt)
				.ThenBy(static job => job.CreatedAt)
				.ThenBy(static job => job.JobId)
				.Take(1);
			var candidates = groupedHeads.Concat(ungroupedHead);

			var candidate = candidates
				.OrderBy(job => IsNoisy(job.GroupId, activeCounts, totalActive, policy))
				.ThenBy(job => GetNoisyInflight(job.GroupId, activeCounts, totalActive, policy))
				.ThenBy(job => policy.GroupRoundRobin ? GetLastServed(queueName, job.GroupId) : 0)
				.ThenBy(static job => job.DueAt)
				.ThenBy(static job => job.CreatedAt)
				.ThenBy(static job => job.JobId)
				.First();

			var job = Acquire(candidate, request, now);
			acquired.Add(job);
			jobCapacities[job.JobName]--;
			queueCapacity--;
			if (policy.GroupRoundRobin && job.GroupId is { } groupId)
				_fairQueueLastServed[(queueName, groupId)] = GetNextSequence(queueName);
		}
	}

	private static bool IsNoisy(
		string? groupId,
		Dictionary<string, int> activeCounts,
		int totalActive,
		FairQueuePolicy policy
	) =>
		groupId is not null &&
		totalActive > 0 &&
		activeCounts.TryGetValue(groupId, out var groupActive) &&
		groupActive >= policy.MinInflightForNoisy &&
		(double)groupActive / totalActive > policy.ConcurrencyShareThreshold;

	private static int GetNoisyInflight(
		string? groupId,
		Dictionary<string, int> activeCounts,
		int totalActive,
		FairQueuePolicy policy
	) =>
		IsNoisy(groupId, activeCounts, totalActive, policy) ? activeCounts[groupId!] : 0;

	private long GetLastServed(string queueName, string? groupId) =>
		groupId is not null && _fairQueueLastServed.TryGetValue((queueName, groupId), out var sequence)
			? sequence
			: 0;

	private long GetNextSequence(string queueName) =>
		_fairQueueLastServed
			.Where(pair => string.Equals(pair.Key.QueueName, queueName, StringComparison.Ordinal))
			.Select(static pair => pair.Value)
			.DefaultIfEmpty()
			.Max() + 1;

	private JobRecord Acquire(JobRecord candidate, JobAcquisitionRequest request, DateTimeOffset now)
	{
		MaterializeSyntheticExecution(candidate);
		var job = candidate with
		{
			State = JobState.Active,
			Attempt = candidate.Attempt + 1,
			WorkerId = request.WorkerId,
			LeaseExpiresAt = now + request.Lease,
			ExecutionTraceId = null,
			ExecutionSpanId = null,
			ExecutionStartedAt = null,
		};
		_jobs[job.JobId] = job;
		CreateExecution(job, now);
		MarkBatchStarted(job.BatchId, now);
		return job;
	}

	// Used by the storage tests' durable-replica proxy. InMemoryJobStorage itself is never a durable replica.
	internal async ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<JobHandle> jobIds,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(jobIds);
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
		cancellationToken.ThrowIfCancellationRequested();
		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			var acquired = new List<JobRecord>(jobIds.Count);
			foreach (var id in jobIds)
			{
				if (!_jobs.TryGetValue(id, out var job))
					continue;
				if (job.State == JobState.Active && job.LeaseExpiresAt <= now)
				{
					InterruptExecution(job);
					job = job with { State = JobState.Pending, WorkerId = null, LeaseExpiresAt = null };
					_jobs[id] = job;
				}

				if (job.State is not (JobState.Pending or JobState.Scheduled) || job.DueAt > now)
					continue;

				MaterializeSyntheticExecution(job);
				job = job with
				{
					State = JobState.Active,
					Attempt = job.Attempt + 1,
					WorkerId = workerId,
					LeaseExpiresAt = now + lease,
					ExecutionTraceId = null,
					ExecutionSpanId = null,
					ExecutionStartedAt = null,
				};
				_jobs[id] = job;
				CreateExecution(job, now);
				MarkBatchStarted(job.BatchId, now);
				acquired.Add(job);
			}

			return acquired;
		}
	}

	/// <inheritdoc />
	public ValueTask SetExecutionTelemetryAsync(
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
		lock (_gate)
		{
			var job = GetOwnedActive(jobId, executionNumber, workerId);
			MaterializeSyntheticExecution(job);
			_jobs[jobId] = job with
			{
				ExecutionTraceId = traceId,
				ExecutionSpanId = spanId,
				ExecutionStartedAt = startedAt,
			};
			UpdateExecution(jobId, executionNumber, execution => execution with
			{
				ExecutionTraceId = traceId,
				ExecutionSpanId = spanId,
				ExecutionStartedAt = startedAt,
			});
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask RenewLeaseAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var job = GetOwnedActive(jobId, executionNumber, workerId);
			_jobs[jobId] = job with { LeaseExpiresAt = timeProvider.GetUtcNow() + lease };
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask CompleteAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	) => CompleteWithContinuationsAsync(jobId, executionNumber, workerId, [], cancellationToken);

	/// <inheritdoc />
	public ValueTask CompleteWithContinuationsAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			var current = GetOwnedActive(jobId, executionNumber, workerId);
			var existingWaiters = GetUnsettledWaiters(jobId);
			var newJobIds = new HashSet<JobHandle>();
			var dependencyEdges = new List<JobContinuationEdge>(additions.Count);
			var trackedAdditions = 0;

			foreach (var addition in additions)
			{
				ValidateNewJob(addition.Job);
				if (!newJobIds.Add(addition.Job.JobId))
					throw new ImmediateJobException($"Job '{addition.Job.JobId}' occurs more than once in the completion buffer.");
				if (addition.Job.State is not (JobState.Pending or JobState.Scheduled))
					throw new ImmediateJobException($"Dynamic continuation '{addition.Job.JobId}' has invalid state '{addition.Job.State}'.");
				if (!Enum.IsDefined(addition.Trigger))
					throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation trigger.");

				if (addition.Options == ContinuationOptions.Detached)
				{
					if (addition.Job.BatchId is not null)
						throw new ImmediateJobException("A detached continuation cannot belong to a batch.");
				}
				else if (addition.Options is ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations)
				{
					if (current.BatchId is null || addition.Job.BatchId != current.BatchId)
						throw new ImmediateJobException("A batch-tracked continuation must belong to the current job's batch.");
					trackedAdditions++;
				}
				else
				{
					throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation option.");
				}

				dependencyEdges.Add(new()
				{
					ChildJobId = addition.Job.JobId,
					ParentJobId = jobId,
					Trigger = addition.Trigger,
					Delay = addition.Delay,
				});
			}

			if (dependencyEdges.Count != 0)
				ValidateEdges([.. additions.Select(static addition => addition.Job)], dependencyEdges, current.BatchId);

			ValidateSplice(existingWaiters, additions.Count(static addition => addition.Options == ContinuationOptions.BeforeContinuations));

			IncrementBatchMembers(current.BatchId, trackedAdditions);
			foreach (var addition in additions)
			{
				_jobs.Add(addition.Job.JobId, NormalizeWaitingJob(addition.Job, dependencyCount: 1));
				if (addition.Options == ContinuationOptions.BeforeContinuations)
					SpliceBeforeWaiters(addition.Job.JobId, existingWaiters);
			}

			_edges.AddRange(dependencyEdges);
			var completedAt = timeProvider.GetUtcNow();
			CompleteExecution(current, JobExecutionState.Succeeded, completedAt);
			TransitionToTerminal(jobId, JobState.Succeeded, error: null, completedAt);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask AddBatchJobAsync(
		JobHandle currentJobId,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(currentJobId, out var current) || current.State != JobState.Active)
				throw new ImmediateJobException($"Job '{currentJobId}' is not currently active.");
			if (current.Attempt != executionNumber)
			{
				throw new ImmediateJobException(string.Create(
					CultureInfo.InvariantCulture,
					$"Execution {executionNumber} cannot add a batch job for '{currentJobId}'; the active execution is {current.Attempt}."
				));
			}

			if (current.BatchId is null || !_batches.ContainsKey(current.BatchId))
				throw new ImmediateJobException("The current job does not belong to a batch.");
			if (options == ContinuationOptions.Detached)
				throw new ImmediateJobException("AddToBatchAsync(JobDetails, ...) cannot create detached work.");
			if (options is not (ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations))
				throw new ArgumentOutOfRangeException(nameof(options));
			ValidateNewJob(job);
			if (job.BatchId != current.BatchId)
				throw new ImmediateJobException("The new job must belong to the current job's batch.");
			if (job.State is JobState.Active or JobState.AwaitingContinuation || IsTerminal(job.State))
				throw new ImmediateJobException($"Concurrent batch member '{job.JobId}' has invalid state '{job.State}'.");

			var existingWaiters = options == ContinuationOptions.BeforeContinuations
				? GetUnsettledWaiters(currentJobId)
				: [];
			ValidateSplice(existingWaiters, options == ContinuationOptions.BeforeContinuations ? 1 : 0);
			IncrementBatchMembers(current.BatchId, 1);
			_jobs.Add(job.JobId, job with { RemainingDependencies = 0 });
			if (options == ContinuationOptions.BeforeContinuations)
				SpliceBeforeWaiters(job.JobId, existingWaiters);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask FailAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			var job = GetOwnedActive(jobId, executionNumber, workerId);
			var completedAt = timeProvider.GetUtcNow();
			CompleteExecution(job, JobExecutionState.Failed, completedAt, error);
			if (nextRetryAt.HasValue)
			{
				var now = timeProvider.GetUtcNow();
				_jobs[jobId] = job with
				{
					State = nextRetryAt <= now ? JobState.Pending : JobState.Scheduled,
					DueAt = nextRetryAt.Value,
					WorkerId = null,
					LeaseExpiresAt = null,
					LastError = error,
					CompletedAt = null,
				};
			}
			else
			{
				TransitionToTerminal(jobId, JobState.Failed, error, completedAt);
			}
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (_recurring.TryGetValue(schedule.Name, out var current))
			{
				if (current.IsCodeDefined && !schedule.IsCodeDefined)
					throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules.");

				schedule = schedule with { IsPaused = current.IsPaused, LastRunAt = current.LastRunAt };
			}

			_recurring[schedule.Name] = schedule;
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var activeNames = activeScheduleNames.ToHashSet(StringComparer.Ordinal);
		lock (_gate)
		{
			var obsoleteNames = _recurring
				.Where(schedule => schedule.Value.IsCodeDefined && !activeNames.Contains(schedule.Key))
				.Select(static schedule => schedule.Key);

			foreach (var name in obsoleteNames)
				_ = _recurring.Remove(name);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_recurring.TryGetValue(name, out var schedule))
				throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
			if (schedule.IsCodeDefined)
				throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");

			_ = _recurring.Remove(name);
		}

		return ValueTask.CompletedTask;
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
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			return
			[
				.. _recurring.Values.Where(x => !x.IsPaused && x.NextRunAt <= now).OrderBy(x => x.NextRunAt).Take(batchSize),
			];
		}
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

		lock (_gate)
		{
			if (!_recurring.TryGetValue(schedule.Name, out var current) || current.NextRunAt != schedule.NextRunAt)
				return false;

			var inserted = job.RecurringKey is null || _recurringKeys.Add(job.RecurringKey);
			if (inserted)
				_jobs[job.JobId] = job;
			_recurring[schedule.Name] = current with { LastRunAt = schedule.NextRunAt, NextRunAt = nextRunAt };
			return inserted;
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			var counts = Enum.GetValues<JobState>().ToDictionary(state => state, state => _jobs.Values.LongCount(x => x.State == state));
			var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromMinutes(2);
			IReadOnlyList<JobServerSnapshot> servers = [.. _servers.Values.Where(x => x.LastHeartbeat >= cutoff)];
			return new JobMonitoringSnapshot
			{
				CapturedAt = timeProvider.GetUtcNow(),
				Counts = counts,
				Recurring = [.. _recurring.Values],
				Servers = servers,
				Capabilities = this.GetCapabilities(),
			};
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			var jobs = _jobs.Values.AsEnumerable();
			if (query.JobId is { } id)
				jobs = jobs.Where(x => x.JobId == query.JobId);
			if (query.State is { } state)
				jobs = jobs.Where(x => x.State == state);
			if (!string.IsNullOrWhiteSpace(query.QueueName))
				jobs = jobs.Where(x => string.Equals(x.QueueName, query.QueueName, StringComparison.Ordinal));
			if (!string.IsNullOrWhiteSpace(query.JobName))
				jobs = jobs.Where(x => string.Equals(x.JobName, query.JobName, StringComparison.Ordinal));

			if (!string.IsNullOrWhiteSpace(query.Search))
				jobs = jobs.Where(x => x.JobName.Contains(query.Search, StringComparison.OrdinalIgnoreCase));

			return
			[
				.. jobs.OrderByDescending(x => x.CreatedAt)
					.ThenBy(x => x.JobId.JobId, StringComparer.Ordinal)
					.Skip(Math.Max(0, query.Skip))
					.Take(Math.Clamp(query.Take, 1, 1000)),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobId,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job))
				return [];

			var executions = _executions.TryGetValue(jobId, out var persisted)
				? persisted.Values.AsEnumerable()
				: [];
			var synthetic = JobExecutionRecords.CreateSynthetic(job);
			if (synthetic is not null && (persisted is null || !persisted.ContainsKey(synthetic.Attempt)))
				executions = executions.Append(synthetic);
			if (query.Attempt is { } attempt)
				executions = executions.Where(execution => execution.Attempt == attempt);

			return [.. executions
				.OrderByDescending(static execution => execution.Attempt)
				.Skip(query.Skip)
				.Take(Math.Min(query.Take, 1000))];
		}
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchStatusAsync(
		BatchHandle batchId,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchId, out var batch))
				return null;

			return ToStatus(batch);
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip, nameof(query));
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0, nameof(query));

		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var batches = _batches.Values.AsEnumerable();
			if (query.State is { } state)
				batches = batches.Where(batch => batch.State == state);
			return
			[
				.. batches.OrderByDescending(static batch => batch.CreatedAt)
					.ThenBy(static batch => batch.BatchId)
					.Skip(query.Skip)
					.Take(query.Take)
					.Select(ToStatus),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		BatchHandle batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_batches.ContainsKey(batchId))
				return [];

			var members = _jobs.Values.Where(job => job.BatchId == batchId);
			if (query.State is { } state)
				members = members.Where(job => job.State == state);

			return
			[
				.. members
					.OrderBy(job => job.CreatedAt)
					.ThenBy(job => job.JobId.JobId, StringComparer.Ordinal)
					.Skip(query.Skip)
					.Take(query.Take)
					.Select(static job => new BatchMemberStatus
					{
						JobId = job.JobId,
						JobName = job.JobName,
						QueueName = job.QueueName,
						State = job.State,
						Attempt = job.Attempt,
						CreatedAt = job.CreatedAt,
						CompletedAt = job.CompletedAt,
						LastError = job.LastError,
					}),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		BatchHandle batchId,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_batches.ContainsKey(batchId))
				return null;

			var members = _jobs.Values
				.Where(job => job.BatchId == batchId)
				.OrderBy(job => job.CreatedAt)
				.ThenBy(job => job.JobId.JobId, StringComparer.Ordinal)
				.ToArray();

			var memberIds = members.Select(static job => job.JobId).ToHashSet();

			return new BatchGraph
			{
				BatchId = batchId,
				Nodes = [.. members.Select(static job => new BatchGraphNode { JobId = job.JobId, JobName = job.JobName, State = job.State })],
				Edges = [.. _edges.Where(edge => memberIds.Contains(edge.ChildJobId)).Select(ToGraphEdge)],
			};
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobId,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job))
				return null;

			return new JobStatus
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
				BatchId = job.BatchId,
				DependsOn = [.. _edges.Where(edge => edge.ChildJobId == jobId).Select(ToGraphEdge)],
			};
		}
	}

	/// <inheritdoc />
	public ValueTask CancelBatchAsync(BatchHandle batchId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchId, out var batch))
				throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
			if (batch.State != BatchState.Executing)
				throw new ImmediateJobException("Only an executing batch can be cancelled.");

			var now = timeProvider.GetUtcNow();
			var jobIds = _jobs.Values
				.Where(job => job.BatchId == batchId && !IsTerminal(job.State))
				.Select(static job => job.JobId)
				.ToArray();
			foreach (var jobId in jobIds)
			{
				if (_jobs.TryGetValue(jobId, out var job) && job.State == JobState.Active)
					CompleteExecution(job, JobExecutionState.Cancelled, now);
				TransitionToTerminal(jobId, JobState.Cancelled, error: null, now, propagateContinuations: false);
			}

			foreach (var jobId in jobIds)
				ProcessTerminalJob(jobId);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask DeleteBatchAsync(BatchHandle batchId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchId, out var batch))
				throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
			if (batch.State == BatchState.Executing)
				throw new ImmediateJobException("Only a terminal batch can be deleted.");

			var jobIds = _jobs.Values
				.Where(job => job.BatchId == batchId)
				.Select(static job => job.JobId)
				.ToHashSet();

			foreach (var jobId in jobIds)
			{
				_ = _jobs.Remove(jobId);
				_ = _executions.Remove(jobId);
			}

			_ = _batches.Remove(batchId);
			RemoveEdgesForJobs(jobIds, [batchId]);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask CancelAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job))
				throw new KeyNotFoundException($"Job '{jobId}' was not found.");
			if (IsTerminal(job.State))
				throw new ImmediateJobException("Only a non-terminal job can be cancelled.");

			var now = timeProvider.GetUtcNow();
			if (job.State == JobState.Active)
				CompleteExecution(job, JobExecutionState.Cancelled, now);
			TransitionToTerminal(jobId, JobState.Cancelled, error: null, now);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask RetryAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job))
				throw new KeyNotFoundException($"Job '{jobId}' was not found.");
			var wasFailed = job.State == JobState.Failed;
			if (!wasFailed && job.State != JobState.Scheduled)
				throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");

			MaterializeSyntheticExecution(job);
			_jobs[jobId] = job with
			{
				State = JobState.Pending,
				DueAt = timeProvider.GetUtcNow(),
				CompletedAt = wasFailed ? null : job.CompletedAt,
				LastError = wasFailed ? null : job.LastError,
			};
			if (wasFailed && job.BatchId is { } batchId && _batches.TryGetValue(batchId, out var batch))
			{
				_batches[batchId] = batch with
				{
					State = BatchState.Executing,
					PendingCount = batch.PendingCount + 1,
					FailedCount = Math.Max(0, batch.FailedCount - 1),
					CompletedAt = null,
				};
			}
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask DeleteAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job))
				throw new KeyNotFoundException($"Job '{jobId}' was not found.");
			if (!IsTerminal(job.State))
				throw new ImmediateJobException("Only terminal jobs can be deleted.");
			if (job.BatchId is not null)
				throw new ImmediateJobException("Batch members cannot be deleted individually.");

			_ = _jobs.Remove(jobId);
			_ = _executions.Remove(jobId);
			if (job.RecurringKey is { } recurringKey)
				_ = _recurringKeys.Remove(recurringKey);
			RemoveEdgesForJobs([jobId]);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			var standaloneJobIds = _jobs.Values
				.Where(static job => job.BatchId is null)
				.Where(
					x =>
						x.CompletedAt is { } completed
						&& (
							(x.State is JobState.Succeeded && completed < now - succeededRetention)
							|| (x.State is JobState.Failed or JobState.Cancelled or JobState.Skipped && completed < now - failedRetention)
						)
				)
				.Select(static job => job.JobId)
				.ToHashSet();

			foreach (var id in standaloneJobIds)
			{
				if (_jobs[id].RecurringKey is { } recurringKey)
					_ = _recurringKeys.Remove(recurringKey);
				_ = _jobs.Remove(id);
				_ = _executions.Remove(id);
			}

			RemoveEdgesForJobs(standaloneJobIds);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			var batchIds = _batches.Values
				.Where(
					batch =>
						batch.CompletedAt is { } completed
						&& (
							(batch.State is BatchState.Succeeded && completed < now - batchSucceededRetention)
							|| (batch.State is BatchState.Failed or BatchState.Cancelled && completed < now - batchFailedRetention)
						)
				)
				.Select(static batch => batch.BatchId)
				.ToHashSet();

			var batchJobIds = _jobs.Values
				.Where(job => job.BatchId is { } batchId && batchIds.Contains(batchId))
				.Select(static job => job.JobId)
				.ToHashSet();

			foreach (var id in batchJobIds)
			{
				_ = _jobs.Remove(id);
				_ = _executions.Remove(id);
			}

			foreach (var batchId in batchIds)
				_ = _batches.Remove(batchId);
			RemoveEdgesForJobs(batchJobIds, batchIds);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
			_servers[server.WorkerId] = server;
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return true;
	}

	private void ValidateNewJob(JobRecord job)
	{
		if (_jobs.ContainsKey(job.JobId))
			throw new ImmediateJobException($"Job '{job.JobId}' already exists.");
	}

	private void ValidateBatch(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges
	)
	{
		if (_batches.ContainsKey(batch.BatchId))
			throw new ImmediateJobException($"Batch '{batch.BatchId}' already exists.");
		if (jobs.Count == 0)
			throw new ImmediateJobException("An atomic batch cannot be empty.");

		var succeeded = jobs.Count(static job => job.State == JobState.Succeeded);
		var failed = jobs.Count(static job => job.State == JobState.Failed);
		var cancelled = jobs.Count(static job => job.State == JobState.Cancelled);
		var skipped = jobs.Count(static job => job.State == JobState.Skipped);
		var pending = jobs.Count - succeeded - failed - cancelled - skipped;

		var expectedState = true switch
		{
			_ when pending != 0 => BatchState.Executing,
			_ when failed != 0 => BatchState.Failed,
			_ when cancelled != 0 => BatchState.Cancelled,
			_ => BatchState.Succeeded,
		};

		if (
			batch.TotalJobs != jobs.Count ||
			batch.PendingCount != pending ||
			batch.SucceededCount != succeeded ||
			batch.FailedCount != failed ||
			batch.CancelledCount != cancelled ||
			batch.SkippedCount != skipped ||
			batch.State != expectedState ||
			((pending == 0) != (batch.CompletedAt is not null))
		)
		{
			throw new ImmediateJobException("A batch header does not match its members or aggregate state.");
		}

		var jobIds = new HashSet<JobHandle>();
		foreach (var job in jobs)
		{
			ValidateNewJob(job);
			if (!jobIds.Add(job.JobId))
				throw new ImmediateJobException($"Job '{job.JobId}' occurs more than once in the batch.");
			if (job.BatchId != batch.BatchId)
				throw new ImmediateJobException($"Job '{job.JobId}' does not belong to batch '{batch.BatchId}'.");
		}

		ValidateEdges(jobs, edges, batch.BatchId);
	}

	private void ValidateEdges(
		IReadOnlyList<JobRecord> newJobs,
		IReadOnlyList<JobContinuationEdge> edges,
		BatchHandle? batchId
	)
	{
		if (batchId is null && edges.Count == 0)
			throw new ImmediateJobException("A continuation must have at least one parent.");

		var newJobIds = newJobs.Select(static job => job.JobId).ToHashSet();
		var logicalEdges = new HashSet<(JobHandle ChildJobId, string ParentKind, ContinuationHandle ParentJobId)>();

		var outgoing = newJobIds.ToDictionary(static id => id, static _ => new List<JobHandle>());
		var incoming = newJobIds.ToDictionary(static id => id, static _ => 0);

		foreach (var edge in edges)
		{
			if (!Enum.IsDefined(edge.Trigger))
				throw new ArgumentOutOfRangeException(nameof(edges), "Unknown continuation trigger.");
			if (!newJobIds.Contains(edge.ChildJobId))
				throw new ImmediateJobException($"Continuation child '{edge.ChildJobId}' is not part of the atomic insert.");

			var hasJobParent = edge.ParentJobId is { };
			var hasBatchParent = edge.ParentBatchId is { };
			if (hasJobParent == hasBatchParent)
				throw new ImmediateJobException("A continuation edge must have exactly one job or batch parent.");

			if (hasJobParent)
			{
				var parentId = edge.ParentJobId!;
				if (parentId == edge.ChildJobId)
					throw new ImmediateJobException($"Continuation job '{edge.ChildJobId}' cannot depend on itself.");
				if (!newJobIds.Contains(parentId) && !_jobs.ContainsKey(parentId))
					throw new KeyNotFoundException($"Continuation parent job '{parentId}' was not found.");
				if (!logicalEdges.Add((edge.ChildJobId, "job", parentId)))
					throw new ImmediateJobException($"Duplicate continuation edge '{parentId}' -> '{edge.ChildJobId}'.");

				if (newJobIds.Contains(parentId))
				{
					outgoing[parentId].Add(edge.ChildJobId);
					incoming[edge.ChildJobId]++;
				}
			}
			else
			{
				var parentId = edge.ParentBatchId!;
				if (!_batches.ContainsKey(parentId))
					throw new KeyNotFoundException($"Continuation parent batch '{parentId}' was not found.");
				if (!logicalEdges.Add((edge.ChildJobId, "batch", parentId)))
					throw new ImmediateJobException($"Duplicate continuation edge from batch '{parentId}' to '{edge.ChildJobId}'.");
			}
		}

		var ready = new Queue<JobHandle>(incoming.Where(static pair => pair.Value == 0).Select(static pair => pair.Key));
		var visited = 0;
		while (ready.TryDequeue(out var parentId))
		{
			visited++;
			foreach (var childId in outgoing[parentId])
			{
				if (--incoming[childId] == 0)
					ready.Enqueue(childId);
			}
		}

		if (visited != newJobIds.Count)
			throw new ImmediateJobException("The continuation graph contains a dependency cycle.");
	}

	private static JobRecord NormalizeWaitingJob(JobRecord job, int dependencyCount) => job with
	{
		State = dependencyCount == 0 ? job.State : JobState.AwaitingContinuation,
		RemainingDependencies = dependencyCount,
		FailedDependencies = 0,
		WorkerId = null,
		LeaseExpiresAt = null,
		CompletedAt = null,
	};

	private bool IsRecoveredBatch(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges
	)
	{
		if (batch.StartedAt is not null ||
			batch.CompletedAt is not null ||
			batch.SucceededCount != 0 ||
			batch.FailedCount != 0 ||
			batch.CancelledCount != 0 ||
			batch.SkippedCount != 0 ||
			jobs.Any(static job => job.State is JobState.Active or JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped))
		{
			return true;
		}

		if (!HasTerminalParent(edges))
			return false;

		var incomingCounts = edges
			.GroupBy(static edge => edge.ChildJobId)
			.ToDictionary(static group => group.Key, static group => group.Count());
		return jobs.Any(job => incomingCounts.TryGetValue(job.JobId, out var incoming) &&
			(job.State != JobState.AwaitingContinuation || job.RemainingDependencies < incoming));
	}

	private bool HasTerminalParent(IEnumerable<JobContinuationEdge> edges) =>
		edges.Any(IsTerminal);

	private void MarkTerminalParentEdgesSettled(IEnumerable<JobContinuationEdge> edges)
	{
		foreach (var edge in edges)
		{
			if (IsTerminal(edge))
			{
				_ = _settledEdges.Add(edge);
			}
		}
	}

	private bool IsTerminal(JobContinuationEdge edge)
	{
		return (
			edge.ParentJobId is { } parentJobId
			&& _jobs.TryGetValue(parentJobId, out var parentJob)
			&& IsTerminal(parentJob.State)
		) || (
			edge.ParentBatchId is { } parentBatchId
			&& _batches.TryGetValue(parentBatchId, out var parentBatch)
			&& IsTerminal(parentBatch.State)
		);
	}

	private void EvaluateAlreadyTerminalParents(IReadOnlyList<JobContinuationEdge> edges)
	{
		foreach (var parentId in edges
			.Where(static edge => edge.ParentJobId is not null)
			.Select(static edge => edge.ParentJobId!)
			.Distinct()
			.Where(parentId => _jobs.TryGetValue(parentId, out var parent) && IsTerminal(parent.State))
			.ToArray())
		{
			ProcessTerminalJob(parentId);
		}

		foreach (var parentId in edges
			.Where(static edge => edge.ParentBatchId is not null)
			.Select(static edge => edge.ParentBatchId!)
			.Distinct()
			.Where(parentId => _batches.TryGetValue(parentId, out var parent) && IsTerminal(parent.State))
			.ToArray())
		{
			ProcessTerminalBatch(parentId);
		}
	}

	private void TransitionToTerminal(
		JobHandle jobId,
		JobState terminalState,
		string? error,
		DateTimeOffset completedAt,
		bool propagateContinuations = true
	)
	{
		if (!IsTerminal(terminalState))
			throw new ArgumentOutOfRangeException(nameof(terminalState));
		if (!_jobs.TryGetValue(jobId, out var job) || IsTerminal(job.State))
			return;

		_jobs[jobId] = job with
		{
			State = terminalState,
			WorkerId = null,
			LeaseExpiresAt = null,
			LastError = error,
			CompletedAt = completedAt,
		};
		UpdateBatchAfterTerminal(job.BatchId, terminalState, completedAt);
		if (propagateContinuations)
			ProcessTerminalJob(jobId);
		RemoveFairQueueCursorWhenBacklogClears(job.QueueName, job.GroupId);
	}

	private void RemoveFairQueueCursorWhenBacklogClears(string queueName, string? groupId)
	{
		if (groupId is null ||
			_jobs.Values.Any(job =>
				string.Equals(job.QueueName, queueName, StringComparison.Ordinal) &&
				string.Equals(job.GroupId, groupId, StringComparison.Ordinal) &&
				job.State is JobState.Pending or JobState.Scheduled or JobState.Active))
		{
			return;
		}

		_ = _fairQueueLastServed.Remove((queueName, groupId));
	}

	private void UpdateBatchAfterTerminal(BatchHandle? batchId, JobState state, DateTimeOffset completedAt)
	{
		if (batchId is null || !_batches.TryGetValue(batchId, out var batch))
			return;

		var pending = Math.Max(0, batch.PendingCount - 1);
		batch = batch with
		{
			PendingCount = pending,
			SucceededCount = batch.SucceededCount + (state == JobState.Succeeded ? 1 : 0),
			FailedCount = batch.FailedCount + (state == JobState.Failed ? 1 : 0),
			CancelledCount = batch.CancelledCount + (state == JobState.Cancelled ? 1 : 0),
			SkippedCount = batch.SkippedCount + (state == JobState.Skipped ? 1 : 0),
		};
		if (pending == 0)
		{
			batch = batch with
			{
				State = batch.FailedCount != 0
					? BatchState.Failed
					: batch.CancelledCount != 0 ? BatchState.Cancelled : BatchState.Succeeded,
				CompletedAt = completedAt,
			};
		}

		_batches[batchId] = batch;
		if (pending == 0)
			ProcessTerminalBatch(batchId);
	}

	private void ProcessTerminalJob(JobHandle parentJobId)
	{
		if (!_jobs.TryGetValue(parentJobId, out var parent) || !IsTerminal(parent.State))
			return;

		foreach (var edge in _edges
			.Where(edge => edge.ParentJobId == parentJobId && !_settledEdges.Contains(edge))
			.ToArray())
		{
			_ = _settledEdges.Add(edge);
			SettleEdge(edge, parentFailed: parent.State == JobState.Failed);
		}
	}

	private void ProcessTerminalBatch(BatchHandle parentBatchId)
	{
		if (!_batches.TryGetValue(parentBatchId, out var parent) || !IsTerminal(parent.State))
			return;

		foreach (var edge in _edges
			.Where(edge => edge.ParentBatchId == parentBatchId && !_settledEdges.Contains(edge))
			.ToArray())
		{
			_ = _settledEdges.Add(edge);
			SettleEdge(edge, parentFailed: parent.State == BatchState.Failed);
		}
	}

	private void SettleEdge(JobContinuationEdge edge, bool parentFailed)
	{
		if (!_jobs.TryGetValue(edge.ChildJobId, out var child) || IsTerminal(child.State))
			return;

		var remaining = Math.Max(0, child.RemainingDependencies - 1);
		if (remaining != 0)
		{
			_jobs[child.JobId] = child with
			{
				RemainingDependencies = remaining,
				FailedDependencies = child.FailedDependencies + (parentFailed ? 1 : 0),
			};
			return;
		}

		var (triggersSatisfied, failedDependencies) = EvaluateIncomingTriggers(child.JobId);
		_jobs[child.JobId] = child with
		{
			State = triggersSatisfied && child.State == JobState.AwaitingContinuation
				? GetReadyState(child)
				: child.State,
			RemainingDependencies = 0,
			FailedDependencies = failedDependencies,
		};
		if (!triggersSatisfied)
			TransitionToTerminal(child.JobId, JobState.Skipped, error: null, timeProvider.GetUtcNow());
	}

	private (bool Satisfied, int FailedDependencies) EvaluateIncomingTriggers(JobHandle childJobId)
	{
		var allTerminal = true;
		var requiresFailure = false;
		var successViolated = false;
		var failedDependencies = 0;
		foreach (var edge in _edges.Where(edge => edge.ChildJobId == childJobId))
		{
			var parentTerminal = false;
			var parentSucceeded = false;
			var parentFailed = false;
			if (edge.ParentJobId is { } parentJobId && _jobs.TryGetValue(parentJobId, out var parentJob))
			{
				parentTerminal = IsTerminal(parentJob.State);
				parentSucceeded = parentJob.State == JobState.Succeeded;
				parentFailed = parentJob.State == JobState.Failed;
			}
			else if (edge.ParentBatchId is { } parentBatchId && _batches.TryGetValue(parentBatchId, out var parentBatch))
			{
				parentTerminal = IsTerminal(parentBatch.State);
				parentSucceeded = parentBatch.State == BatchState.Succeeded;
				parentFailed = parentBatch.State == BatchState.Failed;
			}

			allTerminal &= parentTerminal;
			failedDependencies += parentFailed ? 1 : 0;
			requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
			successViolated |= edge.Trigger == ContinuationTrigger.Success && !parentSucceeded;
		}

		return (
			allTerminal && !successViolated && (!requiresFailure || failedDependencies != 0),
			failedDependencies
		);
	}

	private JobState GetReadyState(JobRecord job) =>
		job.DueAt <= timeProvider.GetUtcNow() ? JobState.Pending : JobState.Scheduled;

	private JobContinuationEdge[] GetUnsettledWaiters(JobHandle parentJobId) =>
	[
		.. _edges.Where(edge => edge.ParentJobId == parentJobId &&
			!_settledEdges.Contains(edge) &&
			_jobs.TryGetValue(edge.ChildJobId, out var child) &&
			!IsTerminal(child.State)),
	];

	private void SpliceBeforeWaiters(
		JobHandle newParentJobId,
		IReadOnlyList<JobContinuationEdge> existingWaiters
	)
	{
		foreach (var existingEdge in existingWaiters)
		{
			if (!_jobs.TryGetValue(existingEdge.ChildJobId, out var child) || IsTerminal(child.State))
				continue;

			_jobs[child.JobId] = child with
			{
				State = JobState.AwaitingContinuation,
				RemainingDependencies = child.RemainingDependencies + 1,
			};
			_edges.Add(new()
			{
				ChildJobId = child.JobId,
				ParentJobId = newParentJobId,
				Trigger = existingEdge.Trigger,
				Delay = TimeSpan.Zero,
			});
		}
	}

	private void ValidateSplice(IReadOnlyList<JobContinuationEdge> existingWaiters, int additions)
	{
		if (additions == 0)
			return;
		foreach (var existingEdge in existingWaiters)
		{
			if (_jobs.TryGetValue(existingEdge.ChildJobId, out var child) &&
				child.RemainingDependencies > int.MaxValue - additions)
			{
				throw new ImmediateJobException($"Continuation dependency count overflow for job '{child.JobId}'.");
			}
		}
	}

	private void IncrementBatchMembers(BatchHandle? batchId, int count)
	{
		if (count == 0)
			return;
		if (batchId is null || !_batches.TryGetValue(batchId, out var batch))
			throw new ImmediateJobException("The current job's batch was not found.");
		if (batch.TotalJobs > int.MaxValue - count || batch.PendingCount > int.MaxValue - count)
			throw new ImmediateJobException($"Batch '{batchId}' member count overflow.");

		_batches[batchId] = batch with
		{
			TotalJobs = batch.TotalJobs + count,
			PendingCount = batch.PendingCount + count,
		};
	}

	private void MarkBatchStarted(BatchHandle? batchId, DateTimeOffset startedAt)
	{
		if (batchId is not null && _batches.TryGetValue(batchId, out var batch) && batch.StartedAt is null)
			_batches[batchId] = batch with { StartedAt = startedAt };
	}

	private static bool IsTerminal(JobState state) =>
		state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;

	private static bool IsTerminal(BatchState state) => state is not BatchState.Executing;

	private static BatchStatus ToStatus(BatchRecord batch) => new()
	{
		BatchId = batch.BatchId,
		State = batch.State,
		Total = batch.TotalJobs,
		Succeeded = batch.SucceededCount,
		Failed = batch.FailedCount,
		Cancelled = batch.CancelledCount,
		Skipped = batch.SkippedCount,
		Remaining = batch.PendingCount,
		CreatedAt = batch.CreatedAt,
		StartedAt = batch.StartedAt,
		CompletedAt = batch.CompletedAt,
		FractionSettled = BatchStatus.CalculateFractionSettled(batch.TotalJobs, batch.PendingCount),
	};

	private static BatchGraphEdge ToGraphEdge(JobContinuationEdge edge) => new()
	{
		ChildJobId = edge.ChildJobId,
		ParentJobId = edge.ParentJobId,
		ParentBatchId = edge.ParentBatchId,
		Trigger = edge.Trigger,
	};

	private void RemoveEdgesForJobs(
		HashSet<JobHandle> jobIds,
		HashSet<BatchHandle>? batchIds = null
	)
	{
		if (jobIds.Count == 0 && (batchIds is null || batchIds.Count == 0))
			return;

		var batches = batchIds ?? [];
		for (var index = _edges.Count - 1; index >= 0; index--)
		{
			var edge = _edges[index];
			if (!jobIds.Contains(edge.ChildJobId) &&
				(edge.ParentJobId is null || !jobIds.Contains(edge.ParentJobId)) &&
				(edge.ParentBatchId is null || !batches.Contains(edge.ParentBatchId)))
			{
				continue;
			}

			_edges.RemoveAt(index);
			_ = _settledEdges.Remove(edge);
		}
	}

	private void CreateExecution(JobRecord job, DateTimeOffset acquiredAt)
	{
		if (!_executions.TryGetValue(job.JobId, out var executions))
			_executions.Add(job.JobId, executions = []);
		if (!executions.TryAdd(job.Attempt, new()
		{
			JobId = job.JobId,
			Attempt = job.Attempt,
			State = JobExecutionState.Active,
			WorkerId = job.WorkerId,
			AcquiredAt = acquiredAt,
		}))
		{
			throw new ImmediateJobException(string.Create(
				CultureInfo.InvariantCulture,
				$"Execution {job.Attempt} for job '{job.JobId}' already exists."
			));
		}
	}

	private void MaterializeSyntheticExecution(JobRecord job)
	{
		var synthetic = JobExecutionRecords.CreateSynthetic(job);
		if (synthetic is null)
			return;
		if (!_executions.TryGetValue(job.JobId, out var executions))
			_executions.Add(job.JobId, executions = []);
		_ = executions.TryAdd(synthetic.Attempt, synthetic);
	}

	private void InterruptExecution(JobRecord job)
	{
		CompleteExecution(job, JobExecutionState.Interrupted, job.LeaseExpiresAt ?? timeProvider.GetUtcNow());
	}

	private void CompleteExecution(
		JobRecord job,
		JobExecutionState state,
		DateTimeOffset completedAt,
		string? error = null
	)
	{
		if (job.Attempt <= 0)
			return;

		MaterializeSyntheticExecution(job);
		UpdateExecution(job.JobId, job.Attempt, execution => execution with
		{
			State = state,
			CompletedAt = completedAt,
			Error = error,
		});
	}

	private void UpdateExecution(
		JobHandle jobId,
		int executionNumber,
		Func<JobExecutionRecord, JobExecutionRecord> update
	)
	{
		if (!_executions.TryGetValue(jobId, out var executions) || !executions.TryGetValue(executionNumber, out var execution))
		{
			throw new ImmediateJobException(string.Create(
				CultureInfo.InvariantCulture,
				$"Execution {executionNumber} for job '{jobId}' was not found."
			));
		}

		executions[executionNumber] = update(execution);
	}

	private JobRecord GetOwnedActive(JobHandle jobId, int executionNumber, string workerId)
	{
		if (!_jobs.TryGetValue(jobId, out var job) || job.State != JobState.Active)
		{
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobId}'.");
		}

		if (job.Attempt != executionNumber)
		{
			throw new ImmediateJobException(string.Create(
				CultureInfo.InvariantCulture,
				$"Execution {executionNumber} does not own active job '{jobId}'; the active execution is {job.Attempt}."
			));
		}

		if (!string.Equals(job.WorkerId, workerId, StringComparison.Ordinal))
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobId}'.");

		return job;
	}

	private ValueTask SetRecurringPausedAsync(string name, bool isPaused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_recurring.TryGetValue(name, out var schedule))
				throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");

			_recurring[name] = schedule with { IsPaused = isPaused };
		}

		return ValueTask.CompletedTask;
	}
}

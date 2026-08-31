using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
///		A best-effort, non-durable, single-node provider intended for development and tests.
/// </summary>
/// <param name="timeProvider">
/// 	The clock used for scheduling, leases, and timestamps.
/// </param>
/// <remarks>
///		Public use should only be done via <see cref="IImmediateJobsStorageBuilder.UseInMemory"/>.
/// </remarks>
[SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Not publicly usable; arguments are validated by internal consumers.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class InMemoryJobStorage(TimeProvider timeProvider) :
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
	public async ValueTask DisposeAsync()
	{
	}

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
	}

	/// <summary>
	///	    Used for testing to pre-load various values to the storage before the test starts.
	/// </summary>
	/// <param name="jobs">
	///	    The jobs that should be loaded in the database.
	/// </param>
	/// <param name="batches">
	///	    The batches that should be loaded in the database.
	/// </param>
	/// <param name="edges">
	///	    The continuation edges that should be loaded in the database.
	/// </param>
	/// <remarks>
	///	    This method should run before any other methods run to initialize test state. Use in regular app code is not
	///	    supported.
	/// </remarks>
	public void LoadPersistedJobState(
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<BatchRecord> batches,
		IReadOnlyList<JobContinuationEdge> edges
	)
	{
		lock (_gate)
		{
			foreach (var b in batches)
				_batches[b.BatchHandle] = b;

			foreach (var j in jobs)
				_jobs[j.JobHandle] = j;

			_edges.AddRange(edges);
		}
	}

	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryAdd(job.JobHandle, job))
				throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
		}
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			ValidateNewJob(job);
			ValidateEdges([job], edges, batchHandle: null);

			var restoreExistingState = HasTerminalParent(edges) &&
				(job.State != JobState.AwaitingContinuation || job.RemainingDependencies < edges.Count);
			_jobs.Add(job.JobHandle, restoreExistingState ? job : NormalizeWaitingJob(job, edges.Count));
			_edges.AddRange(edges);
			if (restoreExistingState)
				MarkTerminalParentEdgesSettled(edges);
			else
				EvaluateAlreadyTerminalParents(edges);
		}
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			ValidateBatch(batch, jobs, edges);

			var restoreExistingState = IsRecoveredBatch(batch, jobs, edges);
			_batches.Add(batch.BatchHandle, batch);

			var incomingCounts = edges
				.GroupBy(static edge => edge.ChildJobHandle)
				.ToDictionary(static group => group.Key, static group => group.Count());

			foreach (var job in jobs)
			{
				_jobs.Add(
					job.JobHandle,
					restoreExistingState
						? job
						: incomingCounts.TryGetValue(job.JobHandle, out var dependencyCount)
						? NormalizeWaitingJob(job, dependencyCount)
						: job with { BatchHandle = batch.BatchHandle, RemainingDependencies = 0, FailedDependencies = 0 }
				);
			}

			_edges.AddRange(edges);
			if (restoreExistingState)
				MarkTerminalParentEdgesSettled(edges);
			else
				EvaluateAlreadyTerminalParents(edges);
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			foreach (var expired in _jobs.Values.Where(x => x.State == JobState.Active && x.LeaseExpiresAt <= now).ToList())
			{
				InterruptExecution(expired);
				_jobs[expired.JobHandle] = expired with
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
			.ThenBy(job => job.JobHandle.Value, StringComparer.Ordinal))
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
				.ToList();
			if (eligible.Count == 0)
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
					.ThenBy(static job => job.JobHandle.Value, StringComparer.Ordinal)
					.First());
			var ungroupedHead = eligible
				.Where(static job => job.GroupId is null)
				.OrderBy(static job => job.DueAt)
				.ThenBy(static job => job.CreatedAt)
				.ThenBy(static job => job.JobHandle.Value, StringComparer.Ordinal)
				.Take(1);
			var candidates = groupedHeads.Concat(ungroupedHead);

			var candidate = candidates
				.OrderBy(job => IsNoisy(job.GroupId, activeCounts, totalActive, policy))
				.ThenBy(job => GetNoisyInflight(job.GroupId, activeCounts, totalActive, policy))
				.ThenBy(job => policy.GroupRoundRobin ? GetLastServed(queueName, job.GroupId) : 0)
				.ThenBy(static job => job.DueAt)
				.ThenBy(static job => job.CreatedAt)
				.ThenBy(static job => job.JobHandle.Value, StringComparer.Ordinal)
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
	)
	{
		return groupId is not null &&
			totalActive > 0 &&
			activeCounts.TryGetValue(groupId, out var groupActive) &&
			groupActive >= policy.MinInflightForNoisy &&
			(double)groupActive / totalActive > policy.ConcurrencyShareThreshold;
	}

	private static int GetNoisyInflight(
		string? groupId,
		Dictionary<string, int> activeCounts,
		int totalActive,
		FairQueuePolicy policy
	)
	{
		return IsNoisy(groupId, activeCounts, totalActive, policy) ? activeCounts[groupId!] : 0;
	}

	private long GetLastServed(string queueName, string? groupId)
	{
		return groupId is not null && _fairQueueLastServed.TryGetValue((queueName, groupId), out var sequence)
			? sequence
			: 0;
	}

	private long GetNextSequence(string queueName)
	{
		return _fairQueueLastServed
			.Where(pair => string.Equals(pair.Key.QueueName, queueName, StringComparison.Ordinal))
			.Select(static pair => pair.Value)
			.DefaultIfEmpty()
			.Max() + 1;
	}

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
		_jobs[job.JobHandle] = job;
		CreateExecution(job, now);
		MarkBatchStarted(job.BatchHandle, now);
		return job;
	}

	/// <inheritdoc />
	public async ValueTask SetExecutionTelemetryAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var job = GetOwnedActive(jobHandle, executionNumber, workerId);
			MaterializeSyntheticExecution(job);
			_jobs[jobHandle] = job with
			{
				ExecutionTraceId = traceId,
				ExecutionSpanId = spanId,
				ExecutionStartedAt = startedAt,
			};
			UpdateExecution(jobHandle, executionNumber, execution => execution with
			{
				ExecutionTraceId = traceId,
				ExecutionSpanId = spanId,
				ExecutionStartedAt = startedAt,
			});
		}
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var job = GetOwnedActive(jobHandle, executionNumber, workerId);
			_jobs[jobHandle] = job with { LeaseExpiresAt = timeProvider.GetUtcNow() + lease };
		}
	}

	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, [], cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask CompleteWithContinuationsAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var current = GetOwnedActive(jobHandle, executionNumber, workerId);
			var existingWaiters = GetUnsettledWaiters(jobHandle);
			var newJobHandles = new HashSet<JobHandle>();
			var dependencyEdges = new List<JobContinuationEdge>(additions.Count);
			var trackedAdditions = 0;

			foreach (var addition in additions)
			{
				ValidateNewJob(addition.Job);
				if (!newJobHandles.Add(addition.Job.JobHandle))
					throw new ImmediateJobException($"Job '{addition.Job.JobHandle}' occurs more than once in the completion buffer.");
				if (addition.Job.State is not (JobState.Pending or JobState.Scheduled))
					throw new ImmediateJobException($"Dynamic continuation '{addition.Job.JobHandle}' has invalid state '{addition.Job.State}'.");
				if (!Enum.IsDefined(addition.Trigger))
					throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation trigger.");

				if (addition.Options == ContinuationOptions.Detached)
				{
					if (addition.Job.BatchHandle is not null)
						throw new ImmediateJobException("A detached continuation cannot belong to a batch.");
				}
				else if (addition.Options is ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations)
				{
					if (current.BatchHandle is null || addition.Job.BatchHandle != current.BatchHandle)
						throw new ImmediateJobException("A batch-tracked continuation must belong to the current job's batch.");
					trackedAdditions++;
				}
				else
				{
					throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation option.");
				}

				dependencyEdges.Add(new()
				{
					ChildJobHandle = addition.Job.JobHandle,
					ParentJobHandle = jobHandle,
					Trigger = addition.Trigger,
					Delay = addition.Delay,
				});
			}

			if (dependencyEdges.Count != 0)
				ValidateEdges([.. additions.Select(static addition => addition.Job)], dependencyEdges, current.BatchHandle);

			ValidateSplice(existingWaiters, additions.Count(static addition => addition.Options == ContinuationOptions.BeforeContinuations));

			IncrementBatchMembers(current.BatchHandle, trackedAdditions);
			foreach (var addition in additions)
			{
				_jobs.Add(addition.Job.JobHandle, NormalizeWaitingJob(addition.Job, dependencyCount: 1));
				if (addition.Options == ContinuationOptions.BeforeContinuations)
					SpliceBeforeWaiters(addition.Job.JobHandle, existingWaiters);
			}

			_edges.AddRange(dependencyEdges);
			var completedAt = timeProvider.GetUtcNow();
			CompleteExecution(current, JobExecutionState.Succeeded, completedAt);
			TransitionToTerminal(jobHandle, JobState.Succeeded, error: null, completedAt);
		}
	}

	/// <inheritdoc />
	public async ValueTask AddBatchJobAsync(
		JobHandle currentJobHandle,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(currentJobHandle, out var current) || current.State != JobState.Active)
				throw new ImmediateJobException($"Job '{currentJobHandle}' is not currently active.");
			if (current.Attempt != executionNumber)
			{
				throw new ImmediateJobException(string.Create(
					CultureInfo.InvariantCulture,
					$"Execution {executionNumber} cannot add a batch job for '{currentJobHandle}'; the active execution is {current.Attempt}."
				));
			}

			if (current.BatchHandle is null || !_batches.ContainsKey(current.BatchHandle))
				throw new ImmediateJobException("The current job does not belong to a batch.");
			if (options == ContinuationOptions.Detached)
				throw new ImmediateJobException("AddToBatchAsync(JobDetails, ...) cannot create detached work.");
			if (options is not (ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations))
				throw new ArgumentOutOfRangeException(nameof(options));
			ValidateNewJob(job);
			if (job.BatchHandle != current.BatchHandle)
				throw new ImmediateJobException("The new job must belong to the current job's batch.");
			if (job.State is JobState.Active or JobState.AwaitingContinuation || IsTerminal(job.State))
				throw new ImmediateJobException($"Concurrent batch member '{job.JobHandle}' has invalid state '{job.State}'.");

			var existingWaiters = options == ContinuationOptions.BeforeContinuations
				? GetUnsettledWaiters(currentJobHandle)
				: [];
			ValidateSplice(existingWaiters, options == ContinuationOptions.BeforeContinuations ? 1 : 0);
			IncrementBatchMembers(current.BatchHandle, 1);
			_jobs.Add(job.JobHandle, job with { RemainingDependencies = 0 });
			if (options == ContinuationOptions.BeforeContinuations)
				SpliceBeforeWaiters(job.JobHandle, existingWaiters);
		}
	}

	/// <inheritdoc />
	public async ValueTask FailAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var job = GetOwnedActive(jobHandle, executionNumber, workerId);
			var completedAt = timeProvider.GetUtcNow();
			CompleteExecution(job, JobExecutionState.Failed, completedAt, error);
			if (nextRetryAt.HasValue)
			{
				var now = timeProvider.GetUtcNow();
				_jobs[jobHandle] = job with
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
				TransitionToTerminal(jobHandle, JobState.Failed, error, completedAt);
			}
		}
	}

	/// <inheritdoc />
	public async ValueTask MergeRecurringSchedulesListAsync(
		IReadOnlyList<RecurringJobSchedule> schedules,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var existingStaticDefinitions = _recurring
				.Where(kvp => kvp.Value.IsCodeDefined)
				.ToDictionary(StringComparer.Ordinal);

			foreach (var schedule in schedules)
			{
				ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(_recurring, schedule.Name, out _);

				current = current switch
				{
					{ } when
						string.Equals(current.Cron, schedule.Cron, StringComparison.Ordinal)
						&& string.Equals(current.TimeZone, schedule.TimeZone, StringComparison.Ordinal) =>
						current with
						{
							JobName = schedule.Name,
							QueueName = schedule.QueueName,
						},

					_ => schedule,
				};

				existingStaticDefinitions.Remove(schedule.Name);
			}

			foreach (var s in existingStaticDefinitions)
				_recurring.Remove(s.Key);
		}
	}

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(_recurring, schedule.Name, out _);

			current = current switch
			{
				{ IsCodeDefined: true } =>
					throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules."),

				{ } =>
					schedule with { IsPaused = current.IsPaused, LastRunAt = current.LastRunAt },

				_ => schedule,
			};
		}
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_recurring.TryGetValue(name, out var schedule))
				throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
			if (schedule.IsCodeDefined)
				throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");

			_ = _recurring.Remove(name);
		}
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await SetRecurringPausedAsync(name, isPaused: true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

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
		await TaskScheduler.Yield();

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
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_recurring.TryGetValue(schedule.Name, out var current) || current.NextRunAt != schedule.NextRunAt)
				return false;

			var inserted = job.RecurringKey is null || _recurringKeys.Add(job.RecurringKey);
			if (inserted)
				_jobs[job.JobHandle] = job;
			_recurring[schedule.Name] = current with { LastRunAt = schedule.NextRunAt, NextRunAt = nextRunAt };
			return inserted;
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

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
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var jobs = _jobs.Values.AsEnumerable();
			if (query.JobHandle is { } id)
				jobs = jobs.Where(x => x.JobHandle == query.JobHandle);
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
					.ThenBy(x => x.JobHandle.Value, StringComparer.Ordinal)
					.Skip(Math.Max(0, query.Skip))
					.Take(Math.Clamp(query.Take, 1, 1000)),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobHandle,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				return [];

			var executions = _executions.TryGetValue(jobHandle, out var persisted)
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
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchHandle, out var batch))
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
		await TaskScheduler.Yield();
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			var batches = _batches.Values.AsEnumerable();
			if (query.State is { } state)
				batches = batches.Where(batch => batch.State == state);
			return
			[
				.. batches.OrderByDescending(static batch => batch.CreatedAt)
					.ThenBy(static batch => batch.BatchHandle.Value, StringComparer.Ordinal)
					.Skip(query.Skip)
					.Take(query.Take)
					.Select(ToStatus),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		BatchHandle batchHandle,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_batches.ContainsKey(batchHandle))
				return [];

			var members = _jobs.Values.Where(job => job.BatchHandle == batchHandle);
			if (query.State is { } state)
				members = members.Where(job => job.State == state);

			return
			[
				.. members
					.OrderBy(job => job.CreatedAt)
					.ThenBy(job => job.JobHandle.Value, StringComparer.Ordinal)
					.Skip(query.Skip)
					.Take(query.Take)
					.Select(static job => new BatchMemberStatus
					{
						JobHandle = job.JobHandle,
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
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_batches.ContainsKey(batchHandle))
				return null;

			var members = _jobs.Values
				.Where(job => job.BatchHandle == batchHandle)
				.OrderBy(job => job.CreatedAt)
				.ThenBy(job => job.JobHandle.Value, StringComparer.Ordinal)
				.ToList();

			var memberIds = members.Select(static job => job.JobHandle).ToHashSet();

			return new BatchGraph
			{
				BatchHandle = batchHandle,
				Nodes = [.. members.Select(static job => new BatchGraphNode { JobHandle = job.JobHandle, JobName = job.JobName, State = job.State })],
				Edges = [.. _edges.Where(edge => memberIds.Contains(edge.ChildJobHandle))],
			};
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobHandle,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				return null;

			return new JobStatus
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
				BatchHandle = job.BatchHandle,
				DependsOn = [.. _edges.Where(edge => edge.ChildJobHandle == jobHandle)],
			};
		}
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchHandle, out var batch))
				throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
			if (batch.State != BatchState.Executing)
				throw new ImmediateJobException("Only an executing batch can be cancelled.");

			var now = timeProvider.GetUtcNow();
			var jobHandles = _jobs.Values
				.Where(job => job.BatchHandle == batchHandle && !IsTerminal(job.State))
				.Select(static job => job.JobHandle)
				.ToList();
			foreach (var jobHandle in jobHandles)
			{
				if (_jobs.TryGetValue(jobHandle, out var job) && job.State == JobState.Active)
					CompleteExecution(job, JobExecutionState.Cancelled, now);
				TransitionToTerminal(jobHandle, JobState.Cancelled, error: null, now, propagateContinuations: false);
			}

			foreach (var jobHandle in jobHandles)
				ProcessTerminalJob(jobHandle);
		}
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchHandle, out var batch))
				throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
			if (batch.State == BatchState.Executing)
				throw new ImmediateJobException("Only a terminal batch can be deleted.");

			var jobHandles = _jobs.Values
				.Where(job => job.BatchHandle == batchHandle)
				.Select(static job => job.JobHandle)
				.ToHashSet();

			foreach (var jobHandle in jobHandles)
			{
				_ = _jobs.Remove(jobHandle);
				_ = _executions.Remove(jobHandle);
			}

			_ = _batches.Remove(batchHandle);
			RemoveEdgesForJobs(jobHandles, [batchHandle]);
		}
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			if (IsTerminal(job.State))
				throw new ImmediateJobException("Only a non-terminal job can be cancelled.");

			var now = timeProvider.GetUtcNow();
			if (job.State == JobState.Active)
				CompleteExecution(job, JobExecutionState.Cancelled, now);
			TransitionToTerminal(jobHandle, JobState.Cancelled, error: null, now);
		}
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			var wasFailed = job.State == JobState.Failed;
			if (!wasFailed && job.State != JobState.Scheduled)
				throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");

			MaterializeSyntheticExecution(job);
			_jobs[jobHandle] = job with
			{
				State = JobState.Pending,
				DueAt = timeProvider.GetUtcNow(),
				CompletedAt = wasFailed ? null : job.CompletedAt,
				LastError = wasFailed ? null : job.LastError,
			};
			if (wasFailed && job.BatchHandle is { } batchHandle && _batches.TryGetValue(batchHandle, out var batch))
			{
				_batches[batchHandle] = batch with
				{
					State = BatchState.Executing,
					PendingCount = batch.PendingCount + 1,
					FailedCount = Math.Max(0, batch.FailedCount - 1),
					CompletedAt = null,
				};
			}
		}
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			if (!IsTerminal(job.State))
				throw new ImmediateJobException("Only terminal jobs can be deleted.");
			if (job.BatchHandle is not null)
				throw new ImmediateJobException("Batch members cannot be deleted individually.");

			_ = _jobs.Remove(jobHandle);
			_ = _executions.Remove(jobHandle);
			if (job.RecurringKey is { } recurringKey)
				_ = _recurringKeys.Remove(recurringKey);
			RemoveEdgesForJobs([jobHandle]);
		}
	}

	/// <inheritdoc />
	public async ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			var standaloneJobHandles = _jobs.Values
				.Where(static job => job.BatchHandle is null)
				.Where(
					x =>
						x.CompletedAt is { } completed
						&& (
							(x.State is JobState.Succeeded && completed < now - succeededRetention)
							|| (x.State is JobState.Failed or JobState.Cancelled or JobState.Skipped && completed < now - failedRetention)
						)
				)
				.Select(static job => job.JobHandle)
				.ToHashSet();

			foreach (var id in standaloneJobHandles)
			{
				if (_jobs[id].RecurringKey is { } recurringKey)
					_ = _recurringKeys.Remove(recurringKey);
				_ = _jobs.Remove(id);
				_ = _executions.Remove(id);
			}

			RemoveEdgesForJobs(standaloneJobHandles);
		}
	}

	/// <inheritdoc />
	public async ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			var batchHandles = _batches.Values
				.Where(
					batch =>
						batch.CompletedAt is { } completed
						&& (
							(batch.State is BatchState.Succeeded && completed < now - batchSucceededRetention)
							|| (batch.State is BatchState.Failed or BatchState.Cancelled && completed < now - batchFailedRetention)
						)
				)
				.Select(static batch => batch.BatchHandle)
				.ToHashSet();

			var batchJobHandles = _jobs.Values
				.Where(job => job.BatchHandle is { } batchHandle && batchHandles.Contains(batchHandle))
				.Select(static job => job.JobHandle)
				.ToHashSet();

			foreach (var id in batchJobHandles)
			{
				_ = _jobs.Remove(id);
				_ = _executions.Remove(id);
			}

			foreach (var batchHandle in batchHandles)
				_ = _batches.Remove(batchHandle);
			RemoveEdgesForJobs(batchJobHandles, batchHandles);
		}
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
			_servers[server.WorkerId] = server;
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		return true;
	}

	private void ValidateNewJob(JobRecord job)
	{
		if (_jobs.ContainsKey(job.JobHandle))
			throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
	}

	private void ValidateBatch(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges
	)
	{
		if (_batches.ContainsKey(batch.BatchHandle))
			throw new ImmediateJobException($"Batch '{batch.BatchHandle}' already exists.");
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

		var jobHandles = new HashSet<JobHandle>();
		foreach (var job in jobs)
		{
			ValidateNewJob(job);
			if (!jobHandles.Add(job.JobHandle))
				throw new ImmediateJobException($"Job '{job.JobHandle}' occurs more than once in the batch.");
			if (job.BatchHandle != batch.BatchHandle)
				throw new ImmediateJobException($"Job '{job.JobHandle}' does not belong to batch '{batch.BatchHandle}'.");
		}

		ValidateEdges(jobs, edges, batch.BatchHandle);
	}

	private void ValidateEdges(
		IReadOnlyList<JobRecord> newJobs,
		IReadOnlyList<JobContinuationEdge> edges,
		BatchHandle? batchHandle
	)
	{
		if (batchHandle is null && edges.Count == 0)
			throw new ImmediateJobException("A continuation must have at least one parent.");

		var newJobHandles = newJobs.Select(static job => job.JobHandle).ToHashSet();
		var logicalEdges = new HashSet<(JobHandle ChildJobHandle, string ParentKind, ContinuationHandle ParentJobHandle)>();

		var outgoing = newJobHandles.ToDictionary(static id => id, static _ => new List<JobHandle>());
		var incoming = newJobHandles.ToDictionary(static id => id, static _ => 0);

		foreach (var edge in edges)
		{
			if (!Enum.IsDefined(edge.Trigger))
				throw new ArgumentOutOfRangeException(nameof(edges), "Unknown continuation trigger.");
			if (!newJobHandles.Contains(edge.ChildJobHandle))
				throw new ImmediateJobException($"Continuation child '{edge.ChildJobHandle}' is not part of the atomic insert.");

			var hasJobParent = edge.ParentJobHandle is { };
			var hasBatchParent = edge.ParentBatchHandle is { };
			if (hasJobParent == hasBatchParent)
				throw new ImmediateJobException("A continuation edge must have exactly one job or batch parent.");

			if (hasJobParent)
			{
				var parentId = edge.ParentJobHandle!;
				if (parentId == edge.ChildJobHandle)
					throw new ImmediateJobException($"Continuation job '{edge.ChildJobHandle}' cannot depend on itself.");
				if (!newJobHandles.Contains(parentId) && !_jobs.ContainsKey(parentId))
					throw new KeyNotFoundException($"Continuation parent job '{parentId}' was not found.");
				if (!logicalEdges.Add((edge.ChildJobHandle, "job", parentId)))
					throw new ImmediateJobException($"Duplicate continuation edge '{parentId}' -> '{edge.ChildJobHandle}'.");

				if (newJobHandles.Contains(parentId))
				{
					outgoing[parentId].Add(edge.ChildJobHandle);
					incoming[edge.ChildJobHandle]++;
				}
			}
			else
			{
				var parentId = edge.ParentBatchHandle!;
				if (!_batches.ContainsKey(parentId))
					throw new KeyNotFoundException($"Continuation parent batch '{parentId}' was not found.");
				if (!logicalEdges.Add((edge.ChildJobHandle, "batch", parentId)))
					throw new ImmediateJobException($"Duplicate continuation edge from batch '{parentId}' to '{edge.ChildJobHandle}'.");
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

		if (visited != newJobHandles.Count)
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
			.GroupBy(static edge => edge.ChildJobHandle)
			.ToDictionary(static group => group.Key, static group => group.Count());
		return jobs.Any(job => incomingCounts.TryGetValue(job.JobHandle, out var incoming) &&
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
			edge.ParentJobHandle is { } parentJobHandle
			&& _jobs.TryGetValue(parentJobHandle, out var parentJob)
			&& IsTerminal(parentJob.State)
		) || (
			edge.ParentBatchHandle is { } parentBatchHandle
			&& _batches.TryGetValue(parentBatchHandle, out var parentBatch)
			&& IsTerminal(parentBatch.State)
		);
	}

	private void EvaluateAlreadyTerminalParents(IReadOnlyList<JobContinuationEdge> edges)
	{
		foreach (var parentId in edges
			.Where(static edge => edge.ParentJobHandle is not null)
			.Select(static edge => edge.ParentJobHandle!)
			.Distinct()
			.Where(parentId => _jobs.TryGetValue(parentId, out var parent) && IsTerminal(parent.State))
			.ToList())
		{
			ProcessTerminalJob(parentId);
		}

		foreach (var parentId in edges
			.Where(static edge => edge.ParentBatchHandle is not null)
			.Select(static edge => edge.ParentBatchHandle!)
			.Distinct()
			.Where(parentId => _batches.TryGetValue(parentId, out var parent) && IsTerminal(parent.State))
			.ToList())
		{
			ProcessTerminalBatch(parentId);
		}
	}

	private void TransitionToTerminal(
		JobHandle jobHandle,
		JobState terminalState,
		string? error,
		DateTimeOffset completedAt,
		bool propagateContinuations = true
	)
	{
		if (!IsTerminal(terminalState))
			throw new ArgumentOutOfRangeException(nameof(terminalState));
		if (!_jobs.TryGetValue(jobHandle, out var job) || IsTerminal(job.State))
			return;

		_jobs[jobHandle] = job with
		{
			State = terminalState,
			WorkerId = null,
			LeaseExpiresAt = null,
			LastError = error,
			CompletedAt = completedAt,
		};
		UpdateBatchAfterTerminal(job.BatchHandle, terminalState, completedAt);
		if (propagateContinuations)
			ProcessTerminalJob(jobHandle);
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

	private void UpdateBatchAfterTerminal(BatchHandle? batchHandle, JobState state, DateTimeOffset completedAt)
	{
		if (batchHandle is null || !_batches.TryGetValue(batchHandle, out var batch))
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

		_batches[batchHandle] = batch;
		if (pending == 0)
			ProcessTerminalBatch(batchHandle);
	}

	private void ProcessTerminalJob(JobHandle parentJobHandle)
	{
		if (!_jobs.TryGetValue(parentJobHandle, out var parent) || !IsTerminal(parent.State))
			return;

		foreach (var edge in _edges
			.Where(edge => edge.ParentJobHandle == parentJobHandle && !_settledEdges.Contains(edge))
			.ToList())
		{
			_ = _settledEdges.Add(edge);
			SettleEdge(edge, parentFailed: parent.State == JobState.Failed);
		}
	}

	private void ProcessTerminalBatch(BatchHandle parentBatchHandle)
	{
		if (!_batches.TryGetValue(parentBatchHandle, out var parent) || !IsTerminal(parent.State))
			return;

		foreach (var edge in _edges
			.Where(edge => edge.ParentBatchHandle == parentBatchHandle && !_settledEdges.Contains(edge))
			.ToList())
		{
			_ = _settledEdges.Add(edge);
			SettleEdge(edge, parentFailed: parent.State == BatchState.Failed);
		}
	}

	private void SettleEdge(JobContinuationEdge edge, bool parentFailed)
	{
		if (!_jobs.TryGetValue(edge.ChildJobHandle, out var child) || IsTerminal(child.State))
			return;

		var remaining = Math.Max(0, child.RemainingDependencies - 1);
		if (remaining != 0)
		{
			_jobs[child.JobHandle] = child with
			{
				RemainingDependencies = remaining,
				FailedDependencies = child.FailedDependencies + (parentFailed ? 1 : 0),
			};
			return;
		}

		var (triggersSatisfied, failedDependencies, delay) = EvaluateIncomingTriggers(child.JobHandle);

		var evaluateDelay = triggersSatisfied && child.State == JobState.AwaitingContinuation
			? timeProvider.GetUtcNow() + delay
			: DateTimeOffset.UnixEpoch;

		var dueAt = child.DueAt > evaluateDelay ? child.DueAt : evaluateDelay;

		_jobs[child.JobHandle] = child with
		{
			State = triggersSatisfied && child.State == JobState.AwaitingContinuation
				? dueAt <= timeProvider.GetUtcNow() ? JobState.Pending : JobState.Scheduled
				: child.State,
			DueAt = dueAt,
			RemainingDependencies = 0,
			FailedDependencies = failedDependencies,
		};

		if (!triggersSatisfied)
			TransitionToTerminal(child.JobHandle, JobState.Skipped, error: null, timeProvider.GetUtcNow());
	}

	private (bool Satisfied, int FailedDependencies, TimeSpan Delay) EvaluateIncomingTriggers(JobHandle childJobHandle)
	{
		var allTerminal = true;
		var requiresFailure = false;
		var successViolated = false;
		var failedDependencies = 0;
		var delay = TimeSpan.Zero;

		foreach (var edge in _edges.Where(edge => edge.ChildJobHandle == childJobHandle))
		{
			var parentTerminal = false;
			var parentSucceeded = false;
			var parentFailed = false;

			if (edge.ParentJobHandle is { } parentJobHandle && _jobs.TryGetValue(parentJobHandle, out var parentJob))
			{
				parentTerminal = IsTerminal(parentJob.State);
				parentSucceeded = parentJob.State == JobState.Succeeded;
				parentFailed = parentJob.State == JobState.Failed;
			}
			else if (edge.ParentBatchHandle is { } parentBatchHandle && _batches.TryGetValue(parentBatchHandle, out var parentBatch))
			{
				parentTerminal = IsTerminal(parentBatch.State);
				parentSucceeded = parentBatch.State == BatchState.Succeeded;
				parentFailed = parentBatch.State == BatchState.Failed;
			}

			allTerminal &= parentTerminal;
			failedDependencies += parentFailed ? 1 : 0;
			requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
			successViolated |= edge.Trigger == ContinuationTrigger.Success && !parentSucceeded;
			delay = edge.Delay > delay ? edge.Delay : delay;
		}

		return (
			allTerminal && !successViolated && (!requiresFailure || failedDependencies != 0),
			failedDependencies,
			delay
		);
	}

	private JobContinuationEdge[] GetUnsettledWaiters(JobHandle parentJobHandle) =>
	[
		.. _edges.Where(edge => edge.ParentJobHandle == parentJobHandle &&
			!_settledEdges.Contains(edge) &&
			_jobs.TryGetValue(edge.ChildJobHandle, out var child) &&
			!IsTerminal(child.State)),
	];

	private void SpliceBeforeWaiters(
		JobHandle newParentJobHandle,
		IReadOnlyList<JobContinuationEdge> existingWaiters
	)
	{
		foreach (var existingEdge in existingWaiters)
		{
			if (!_jobs.TryGetValue(existingEdge.ChildJobHandle, out var child) || IsTerminal(child.State))
				continue;

			_jobs[child.JobHandle] = child with
			{
				State = JobState.AwaitingContinuation,
				RemainingDependencies = child.RemainingDependencies + 1,
			};
			_edges.Add(new()
			{
				ChildJobHandle = child.JobHandle,
				ParentJobHandle = newParentJobHandle,
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
			if (_jobs.TryGetValue(existingEdge.ChildJobHandle, out var child) &&
				child.RemainingDependencies > int.MaxValue - additions)
			{
				throw new ImmediateJobException($"Continuation dependency count overflow for job '{child.JobHandle}'.");
			}
		}
	}

	private void IncrementBatchMembers(BatchHandle? batchHandle, int count)
	{
		if (count == 0)
			return;
		if (batchHandle is null || !_batches.TryGetValue(batchHandle, out var batch))
			throw new ImmediateJobException("The current job's batch was not found.");
		if (batch.TotalJobs > int.MaxValue - count || batch.PendingCount > int.MaxValue - count)
			throw new ImmediateJobException($"Batch '{batchHandle}' member count overflow.");

		_batches[batchHandle] = batch with
		{
			TotalJobs = batch.TotalJobs + count,
			PendingCount = batch.PendingCount + count,
		};
	}

	private void MarkBatchStarted(BatchHandle? batchHandle, DateTimeOffset startedAt)
	{
		if (batchHandle is not null && _batches.TryGetValue(batchHandle, out var batch) && batch.StartedAt is null)
			_batches[batchHandle] = batch with { StartedAt = startedAt };
	}

	private static bool IsTerminal(JobState state) =>
		state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;

	private static bool IsTerminal(BatchState state) => state is not BatchState.Executing;

	private static BatchStatus ToStatus(BatchRecord batch) =>
		new()
		{
			BatchHandle = batch.BatchHandle,
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

	private void RemoveEdgesForJobs(
		HashSet<JobHandle> jobHandles,
		HashSet<BatchHandle>? batchHandles = null
	)
	{
		if (jobHandles.Count == 0 && (batchHandles is null || batchHandles.Count == 0))
			return;

		var batches = batchHandles ?? [];
		for (var index = _edges.Count - 1; index >= 0; index--)
		{
			var edge = _edges[index];
			if (!jobHandles.Contains(edge.ChildJobHandle) &&
				(edge.ParentJobHandle is null || !jobHandles.Contains(edge.ParentJobHandle)) &&
				(edge.ParentBatchHandle is null || !batches.Contains(edge.ParentBatchHandle)))
			{
				continue;
			}

			_edges.RemoveAt(index);
			_ = _settledEdges.Remove(edge);
		}
	}

	private void CreateExecution(JobRecord job, DateTimeOffset acquiredAt)
	{
		if (!_executions.TryGetValue(job.JobHandle, out var executions))
			_executions.Add(job.JobHandle, executions = []);
		if (!executions.TryAdd(job.Attempt, new()
		{
			JobHandle = job.JobHandle,
			Attempt = job.Attempt,
			State = JobExecutionState.Active,
			WorkerId = job.WorkerId,
			AcquiredAt = acquiredAt,
		}))
		{
			throw new ImmediateJobException(string.Create(
				CultureInfo.InvariantCulture,
				$"Execution {job.Attempt} for job '{job.JobHandle}' already exists."
			));
		}
	}

	private void MaterializeSyntheticExecution(JobRecord job)
	{
		var synthetic = JobExecutionRecords.CreateSynthetic(job);
		if (synthetic is null)
			return;
		if (!_executions.TryGetValue(job.JobHandle, out var executions))
			_executions.Add(job.JobHandle, executions = []);
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
		UpdateExecution(job.JobHandle, job.Attempt, execution => execution with
		{
			State = state,
			CompletedAt = completedAt,
			Error = error,
		});
	}

	private void UpdateExecution(
		JobHandle jobHandle,
		int executionNumber,
		Func<JobExecutionRecord, JobExecutionRecord> update
	)
	{
		if (!_executions.TryGetValue(jobHandle, out var executions) || !executions.TryGetValue(executionNumber, out var execution))
		{
			throw new ImmediateJobException(string.Create(
				CultureInfo.InvariantCulture,
				$"Execution {executionNumber} for job '{jobHandle}' was not found."
			));
		}

		executions[executionNumber] = update(execution);
	}

	private JobRecord GetOwnedActive(JobHandle jobHandle, int executionNumber, string workerId)
	{
		if (!_jobs.TryGetValue(jobHandle, out var job) || job.State != JobState.Active)
		{
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
		}

		if (job.Attempt != executionNumber)
		{
			throw new ImmediateJobException(string.Create(
				CultureInfo.InvariantCulture,
				$"Execution {executionNumber} does not own active job '{jobHandle}'; the active execution is {job.Attempt}."
			));
		}

		if (!string.Equals(job.WorkerId, workerId, StringComparison.Ordinal))
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");

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

namespace Immediate.Jobs.Shared;

#pragma warning disable IDE0391 // Keep synchronous storage methods async for .NET 11 runtime-async state machines.

/// <summary>
/// A best-effort, non-durable, single-node provider intended for development and tests.
/// </summary>
public sealed class InMemoryJobStorage(TimeProvider timeProvider) : IJobStorage, IJobStorageReplica
{
	private readonly Lock _gate = new();
	private readonly Dictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);
	private readonly Dictionary<string, JobBatchRecord> _batches = new(StringComparer.Ordinal);
	private readonly List<JobContinuationEdge> _edges = [];
	private readonly HashSet<JobContinuationEdge> _settledEdges = [];
	private readonly Dictionary<string, RecurringJobSchedule> _recurring = new(StringComparer.Ordinal);
	private readonly Dictionary<string, JobServerSnapshot> _servers = new(StringComparer.Ordinal);
	private readonly HashSet<string> _recurringKeys = new(StringComparer.Ordinal);

	/// <inheritdoc />
	public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(job);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_jobs.TryAdd(job.Id, job))
				throw new ImmediateJobException($"Job '{job.Id}' already exists.");
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
		ArgumentNullException.ThrowIfNull(job);
		ArgumentNullException.ThrowIfNull(edges);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			ValidateNewJob(job);
			ValidateEdges([job], edges, batchId: null);

			var restoreExistingState = HasTerminalParent(edges) &&
				(job.State != JobState.AwaitingContinuation || job.RemainingDependencies < edges.Count);
			_jobs.Add(job.Id, restoreExistingState ? job : NormalizeWaitingJob(job, edges.Count));
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
		JobBatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(batch);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(edges);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			ValidateBatch(batch, jobs, edges);

			var restoreExistingState = IsRecoveredBatch(batch, jobs, edges);
			_batches.Add(batch.Id, batch);
			var incomingCounts = edges
				.GroupBy(static edge => edge.ChildJobId, StringComparer.Ordinal)
				.ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
			foreach (var job in jobs)
			{
				_jobs.Add(
					job.Id,
					restoreExistingState
						? job
						: incomingCounts.TryGetValue(job.Id, out var dependencyCount)
						? NormalizeWaitingJob(job, dependencyCount)
						: job with { BatchId = batch.Id, RemainingDependencies = 0, FailedDependencies = 0 }
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
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Lease, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.BatchSize, 0);
		cancellationToken.ThrowIfCancellationRequested();
		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			foreach (var expired in _jobs.Values.Where(x => x.State == JobState.Active && x.LeaseExpiresAt <= now).ToArray())
			{
				_jobs[expired.Id] = expired with
				{
					State = JobState.Pending,
					WorkerId = null,
					LeaseExpiresAt = null,
				};
			}

			var acquired = new List<JobRecord>(request.BatchSize);
			foreach (var queue in request.Queues)
			{
				var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
				if (queueCapacity <= 0)
					continue;

				var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
				foreach (var candidate in _jobs.Values
					.Where(job => job.QueueName == queue.QueueName &&
						jobCapacities.ContainsKey(job.JobName) &&
						job.State is JobState.Pending or JobState.Scheduled &&
						job.DueAt <= now)
					.OrderBy(job => job.DueAt)
					.ThenBy(job => job.CreatedAt)
					.ThenBy(job => job.Id))
				{
					if (queueCapacity == 0)
						break;
					if (jobCapacities[candidate.JobName] <= 0)
						continue;

					var job = candidate with
					{
						State = JobState.Active,
						Attempt = candidate.Attempt + 1,
						WorkerId = request.WorkerId,
						LeaseExpiresAt = now + request.Lease,
					};
					_jobs[job.Id] = job;
					MarkBatchStarted(job.BatchId, now);
					acquired.Add(job);
					jobCapacities[job.JobName]--;
					queueCapacity--;
				}
			}

			return acquired;
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<string> jobIds,
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
					job = job with { State = JobState.Pending, WorkerId = null, LeaseExpiresAt = null };
					_jobs[id] = job;
				}

				if (job.State is not (JobState.Pending or JobState.Scheduled) || job.DueAt > now)
					continue;

				job = job with
				{
					State = JobState.Active,
					Attempt = job.Attempt + 1,
					WorkerId = workerId,
					LeaseExpiresAt = now + lease,
				};
				_jobs[id] = job;
				MarkBatchStarted(job.BatchId, now);
				acquired.Add(job);
			}

			return acquired;
		}
	}

	/// <inheritdoc />
	public ValueTask RenewLeaseAsync(string jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var job = GetOwnedActive(jobId, workerId);
			_jobs[jobId] = job with { LeaseExpiresAt = timeProvider.GetUtcNow() + lease };
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask CompleteAsync(string jobId, string workerId, CancellationToken cancellationToken = default)
		=> CompleteWithContinuationsAsync(jobId, workerId, [], cancellationToken);

	/// <inheritdoc />
	public ValueTask CompleteWithContinuationsAsync(
		string jobId,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(additions);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var current = GetOwnedActive(jobId, workerId);
			var existingWaiters = GetUnsettledWaiters(jobId);
			var newJobIds = new HashSet<string>(StringComparer.Ordinal);
			var dependencyEdges = new List<JobContinuationEdge>(additions.Count);
			var trackedAdditions = 0;
			foreach (var addition in additions)
			{
				ArgumentNullException.ThrowIfNull(addition);
				ArgumentNullException.ThrowIfNull(addition.Job);
				ValidateNewJob(addition.Job);
				if (!newJobIds.Add(addition.Job.Id))
					throw new ImmediateJobException($"Job '{addition.Job.Id}' occurs more than once in the completion buffer.");
				if (addition.Job.State is not (JobState.Pending or JobState.Scheduled))
					throw new ImmediateJobException($"Dynamic continuation '{addition.Job.Id}' has invalid state '{addition.Job.State}'.");
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
					ChildJobId = addition.Job.Id,
					ParentJobId = jobId,
					Trigger = addition.Trigger,
				});
			}

			if (dependencyEdges.Count != 0)
				ValidateEdges([.. additions.Select(static addition => addition.Job)], dependencyEdges, current.BatchId);
			ValidateSplice(existingWaiters, additions.Count(static addition => addition.Options == ContinuationOptions.BeforeContinuations));

			IncrementBatchMembers(current.BatchId, trackedAdditions);
			foreach (var addition in additions)
			{
				_jobs.Add(addition.Job.Id, NormalizeWaitingJob(addition.Job, dependencyCount: 1));
				if (addition.Options == ContinuationOptions.BeforeContinuations)
					SpliceBeforeWaiters(addition.Job.Id, existingWaiters);
			}

			_edges.AddRange(dependencyEdges);
			TransitionToTerminal(jobId, JobState.Succeeded, error: null, timeProvider.GetUtcNow());
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask AddBatchJobAsync(
		string currentJobId,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(job);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_jobs.TryGetValue(currentJobId, out var current) || current.State != JobState.Active)
				throw new ImmediateJobException($"Job '{currentJobId}' is not currently active.");
			if (current.BatchId is null || !_batches.ContainsKey(current.BatchId))
				throw new ImmediateJobException("The current job does not belong to a batch.");
			if (options == ContinuationOptions.Detached)
				throw new ImmediateJobException("IJOB020: AddToBatchAsync(JobDetails, ...) cannot create detached work.");
			if (options is not (ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations))
				throw new ArgumentOutOfRangeException(nameof(options));
			ValidateNewJob(job);
			if (job.BatchId != current.BatchId)
				throw new ImmediateJobException("The new job must belong to the current job's batch.");
			if (job.State is JobState.Active or JobState.AwaitingContinuation || IsTerminal(job.State))
				throw new ImmediateJobException($"Concurrent batch member '{job.Id}' has invalid state '{job.State}'.");

			var existingWaiters = options == ContinuationOptions.BeforeContinuations
				? GetUnsettledWaiters(currentJobId)
				: [];
			ValidateSplice(existingWaiters, options == ContinuationOptions.BeforeContinuations ? 1 : 0);
			IncrementBatchMembers(current.BatchId, 1);
			_jobs.Add(job.Id, job with { RemainingDependencies = 0 });
			if (options == ContinuationOptions.BeforeContinuations)
				SpliceBeforeWaiters(job.Id, existingWaiters);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask FailAsync(
		string jobId,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var job = GetOwnedActive(jobId, workerId);
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
				TransitionToTerminal(jobId, JobState.Failed, error, timeProvider.GetUtcNow());
			}
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(schedule);
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
		ArgumentNullException.ThrowIfNull(activeScheduleNames);
		cancellationToken.ThrowIfCancellationRequested();
		var activeNames = activeScheduleNames.ToHashSet(StringComparer.Ordinal);
		lock (_gate)
		{
			var obsoleteNames = _recurring
				.Where(schedule => schedule.Value.IsCodeDefined && !activeNames.Contains(schedule.Key))
				.Select(static schedule => schedule.Key)
				.ToArray();
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
			if (_recurring.TryGetValue(name, out var schedule) && schedule.IsCodeDefined)
				throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");

			_ = _recurring.Remove(name);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default) =>
		SetRecurringPausedAsync(name, true, cancellationToken);

	/// <inheritdoc />
	public ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default) =>
		SetRecurringPausedAsync(name, false, cancellationToken);

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
		ArgumentNullException.ThrowIfNull(schedule);
		ArgumentNullException.ThrowIfNull(job);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_recurring.TryGetValue(schedule.Name, out var current) || current.NextRunAt != schedule.NextRunAt)
				return false;

			var inserted = job.RecurringKey is null || _recurringKeys.Add(job.RecurringKey);
			if (inserted)
				_jobs[job.Id] = job;
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
			return new(timeProvider.GetUtcNow(), counts, [.. _recurring.Values], servers);
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(query);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var jobs = _jobs.Values.AsEnumerable();
			if (query.Id is { } id)
				jobs = jobs.Where(x => x.Id == id);
			if (query.State is { } state)
				jobs = jobs.Where(x => x.State == state);
			if (!string.IsNullOrWhiteSpace(query.QueueName))
				jobs = jobs.Where(x => x.QueueName == query.QueueName);

			if (!string.IsNullOrWhiteSpace(query.Search))
				jobs = jobs.Where(x => x.JobName.Contains(query.Search, StringComparison.OrdinalIgnoreCase));

			return
			[
				.. jobs.OrderByDescending(x => x.CreatedAt).Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 1000)),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchStatusAsync(
		string batchId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
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
		JobBatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			var batches = _batches.Values.AsEnumerable();
			if (query.State is { } state)
				batches = batches.Where(batch => batch.State == state);
			return
			[
				.. batches.OrderByDescending(static batch => batch.CreatedAt)
					.ThenBy(static batch => batch.Id, StringComparer.Ordinal)
					.Skip(query.Skip)
					.Take(query.Take)
					.Select(ToStatus),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0);
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
					.ThenBy(job => job.Id, StringComparer.Ordinal)
					.Skip(query.Skip)
					.Take(query.Take)
					.Select(static job => new BatchMemberStatus(
						job.Id,
						job.JobName,
						job.QueueName,
						job.State,
						job.Attempt,
						job.CreatedAt,
						job.CompletedAt,
						job.LastError
					)),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		string batchId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_batches.ContainsKey(batchId))
				return null;

			var members = _jobs.Values
				.Where(job => job.BatchId == batchId)
				.OrderBy(job => job.CreatedAt)
				.ThenBy(job => job.Id, StringComparer.Ordinal)
				.ToArray();
			var memberIds = members.Select(static job => job.Id).ToHashSet(StringComparer.Ordinal);
			return new(
				batchId,
				[.. members.Select(static job => new BatchGraphNode(job.Id, job.JobName, job.State))],
				[.. _edges.Where(edge => memberIds.Contains(edge.ChildJobId)).Select(ToGraphEdge)]
			);
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		string jobId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job))
				return null;

			return new(
				job.Id,
				job.JobName,
				job.QueueName,
				job.State,
				job.Attempt,
				MaxAttempts: 0,
				job.CreatedAt,
				job.DueAt,
				job.CompletedAt,
				job.LastError,
				job.BatchId,
				[.. _edges.Where(edge => edge.ChildJobId == jobId).Select(ToGraphEdge)]
			);
		}
	}

	/// <inheritdoc />
	public ValueTask CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_batches.TryGetValue(batchId, out var batch))
				throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
			if (batch.State != BatchState.Executing)
				throw new ImmediateJobException("Only an executing batch can be cancelled.");

			foreach (var jobId in _jobs.Values
				.Where(job => job.BatchId == batchId && !IsTerminal(job.State))
				.Select(static job => job.Id)
				.ToArray())
			{
				TransitionToTerminal(jobId, JobState.Cancelled, error: null, timeProvider.GetUtcNow());
			}
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_batches.TryGetValue(batchId, out var batch))
				throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
			if (batch.State == BatchState.Executing)
				throw new ImmediateJobException("Only a terminal batch can be deleted.");

			var jobIds = _jobs.Values
				.Where(job => job.BatchId == batchId)
				.Select(static job => job.Id)
				.ToHashSet(StringComparer.Ordinal);
			foreach (var jobId in jobIds)
				_ = _jobs.Remove(jobId);
			_ = _batches.Remove(batchId);
			RemoveEdgesForJobs(jobIds, [batchId]);
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job) || job.State != JobState.Failed)
				throw new ImmediateJobException("Only failed jobs can be retried.");

			_jobs[jobId] = job with
			{
				State = JobState.Pending,
				DueAt = timeProvider.GetUtcNow(),
				CompletedAt = null,
				LastError = null,
			};
			if (job.BatchId is { } batchId && _batches.TryGetValue(batchId, out var batch))
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
	public ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job))
				return ValueTask.CompletedTask;
			if (!IsTerminal(job.State))
				throw new ImmediateJobException("Only terminal jobs can be deleted.");
			if (job.BatchId is not null)
				throw new ImmediateJobException("Batch members cannot be deleted individually.");

			_ = _jobs.Remove(jobId);
			RemoveEdgesForJobs(new(StringComparer.Ordinal) { jobId });
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask PurgeAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
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
				.Where(batch => batch.CompletedAt is { } completed &&
					(batch.State == BatchState.Succeeded && completed < now - batchSucceededRetention ||
					 batch.State is BatchState.Failed or BatchState.Cancelled && completed < now - batchFailedRetention))
				.Select(static batch => batch.Id)
				.ToHashSet(StringComparer.Ordinal);
			var batchJobIds = _jobs.Values
				.Where(job => job.BatchId is { } batchId && batchIds.Contains(batchId))
				.Select(static job => job.Id)
				.ToHashSet(StringComparer.Ordinal);
			foreach (var id in batchJobIds)
				_ = _jobs.Remove(id);
			foreach (var batchId in batchIds)
				_ = _batches.Remove(batchId);
			RemoveEdgesForJobs(batchJobIds, batchIds);

			var standaloneJobIds = _jobs.Values
				.Where(static job => job.BatchId is null)
				.Where(x => x.CompletedAt is { } completed &&
					(x.State == JobState.Succeeded && completed < now - succeededRetention ||
					 x.State is JobState.Failed or JobState.Cancelled && completed < now - failedRetention))
				.Select(static job => job.Id)
				.ToHashSet(StringComparer.Ordinal);
			foreach (var id in standaloneJobIds)
				_ = _jobs.Remove(id);
			RemoveEdgesForJobs(standaloneJobIds);
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
		ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);
		ArgumentException.ThrowIfNullOrWhiteSpace(job.JobName);
		if (_jobs.ContainsKey(job.Id))
			throw new ImmediateJobException($"Job '{job.Id}' already exists.");
	}

	private void ValidateBatch(
		JobBatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batch.Id);
		if (_batches.ContainsKey(batch.Id))
			throw new ImmediateJobException($"Batch '{batch.Id}' already exists.");
		if (jobs.Count == 0)
			throw new ImmediateJobException("An atomic batch cannot be empty.");
		var succeeded = jobs.Count(static job => job.State == JobState.Succeeded);
		var failed = jobs.Count(static job => job.State == JobState.Failed);
		var cancelled = jobs.Count(static job => job.State == JobState.Cancelled);
		var pending = jobs.Count - succeeded - failed - cancelled;
		var expectedState = pending != 0
			? BatchState.Executing
			: failed != 0 ? BatchState.Failed : cancelled != 0 ? BatchState.Cancelled : BatchState.Succeeded;
		if (batch.TotalJobs != jobs.Count ||
			batch.PendingCount != pending ||
			batch.SucceededCount != succeeded ||
			batch.FailedCount != failed ||
			batch.CancelledCount != cancelled ||
			batch.State != expectedState ||
			(pending == 0) != (batch.CompletedAt is not null))
		{
			throw new ImmediateJobException("A batch header does not match its members or aggregate state.");
		}

		var jobIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var job in jobs)
		{
			ValidateNewJob(job);
			if (!jobIds.Add(job.Id))
				throw new ImmediateJobException($"Job '{job.Id}' occurs more than once in the batch.");
			if (job.BatchId != batch.Id)
				throw new ImmediateJobException($"Job '{job.Id}' does not belong to batch '{batch.Id}'.");
		}

		ValidateEdges(jobs, edges, batch.Id);
	}

	private void ValidateEdges(
		IReadOnlyList<JobRecord> newJobs,
		IReadOnlyList<JobContinuationEdge> edges,
		string? batchId
	)
	{
		if (batchId is null && edges.Count == 0)
			throw new ImmediateJobException("A continuation must have at least one parent.");

		var newJobIds = newJobs.Select(static job => job.Id).ToHashSet(StringComparer.Ordinal);
		var logicalEdges = new HashSet<(string Child, string ParentKind, string Parent)>(
			EqualityComparer<(string Child, string ParentKind, string Parent)>.Default
		);
		var outgoing = newJobIds.ToDictionary(static id => id, static _ => new List<string>(), StringComparer.Ordinal);
		var incoming = newJobIds.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
		foreach (var edge in edges)
		{
			ArgumentNullException.ThrowIfNull(edge);
			ArgumentException.ThrowIfNullOrWhiteSpace(edge.ChildJobId);
			if (!Enum.IsDefined(edge.Trigger))
				throw new ArgumentOutOfRangeException(nameof(edges), "Unknown continuation trigger.");
			if (!newJobIds.Contains(edge.ChildJobId))
				throw new ImmediateJobException($"Continuation child '{edge.ChildJobId}' is not part of the atomic insert.");

			var hasJobParent = !string.IsNullOrWhiteSpace(edge.ParentJobId);
			var hasBatchParent = !string.IsNullOrWhiteSpace(edge.ParentBatchId);
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

		var ready = new Queue<string>(incoming.Where(static pair => pair.Value == 0).Select(static pair => pair.Key));
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
			throw new ImmediateJobException("IJOB018: The continuation graph contains a dependency cycle.");
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
		JobBatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges
	)
	{
		if (batch.StartedAt is not null ||
			batch.CompletedAt is not null ||
			batch.SucceededCount != 0 ||
			batch.FailedCount != 0 ||
			batch.CancelledCount != 0 ||
			jobs.Any(static job => job.State is JobState.Active or JobState.Succeeded or JobState.Failed or JobState.Cancelled))
		{
			return true;
		}

		if (!HasTerminalParent(edges))
			return false;

		var incomingCounts = edges
			.GroupBy(static edge => edge.ChildJobId, StringComparer.Ordinal)
			.ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
		return jobs.Any(job => incomingCounts.TryGetValue(job.Id, out var incoming) &&
			(job.State != JobState.AwaitingContinuation || job.RemainingDependencies < incoming));
	}

	private bool HasTerminalParent(IEnumerable<JobContinuationEdge> edges) => edges.Any(edge =>
		edge.ParentJobId is { } parentJobId &&
			_jobs.TryGetValue(parentJobId, out var parentJob) &&
			IsTerminal(parentJob.State) ||
		edge.ParentBatchId is { } parentBatchId &&
			_batches.TryGetValue(parentBatchId, out var parentBatch) &&
			IsTerminal(parentBatch.State));

	private void MarkTerminalParentEdgesSettled(IEnumerable<JobContinuationEdge> edges)
	{
		foreach (var edge in edges)
		{
			if (edge.ParentJobId is { } parentJobId &&
				_jobs.TryGetValue(parentJobId, out var parentJob) &&
				IsTerminal(parentJob.State) ||
				edge.ParentBatchId is { } parentBatchId &&
				_batches.TryGetValue(parentBatchId, out var parentBatch) &&
				IsTerminal(parentBatch.State))
			{
				_ = _settledEdges.Add(edge);
			}
		}
	}

	private void EvaluateAlreadyTerminalParents(IReadOnlyList<JobContinuationEdge> edges)
	{
		foreach (var parentId in edges
			.Where(static edge => edge.ParentJobId is not null)
			.Select(static edge => edge.ParentJobId!)
			.Distinct(StringComparer.Ordinal)
			.Where(parentId => _jobs.TryGetValue(parentId, out var parent) && IsTerminal(parent.State))
			.ToArray())
		{
			ProcessTerminalJob(parentId);
		}

		foreach (var parentId in edges
			.Where(static edge => edge.ParentBatchId is not null)
			.Select(static edge => edge.ParentBatchId!)
			.Distinct(StringComparer.Ordinal)
			.Where(parentId => _batches.TryGetValue(parentId, out var parent) && IsTerminal(parent.State))
			.ToArray())
		{
			ProcessTerminalBatch(parentId);
		}
	}

	private void TransitionToTerminal(
		string jobId,
		JobState terminalState,
		string? error,
		DateTimeOffset completedAt
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
		ProcessTerminalJob(jobId);
	}

	private void UpdateBatchAfterTerminal(string? batchId, JobState state, DateTimeOffset completedAt)
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

	private void ProcessTerminalJob(string parentJobId)
	{
		if (!_jobs.TryGetValue(parentJobId, out var parent) || !IsTerminal(parent.State))
			return;

		foreach (var edge in _edges
			.Where(edge => edge.ParentJobId == parentJobId && !_settledEdges.Contains(edge))
			.ToArray())
		{
			_ = _settledEdges.Add(edge);
			SettleEdge(
				edge,
				parentSucceeded: parent.State == JobState.Succeeded,
				parentFailed: parent.State == JobState.Failed
			);
		}
	}

	private void ProcessTerminalBatch(string parentBatchId)
	{
		if (!_batches.TryGetValue(parentBatchId, out var parent) || !IsTerminal(parent.State))
			return;

		foreach (var edge in _edges
			.Where(edge => edge.ParentBatchId == parentBatchId && !_settledEdges.Contains(edge))
			.ToArray())
		{
			_ = _settledEdges.Add(edge);
			SettleEdge(
				edge,
				parentSucceeded: parent.State == BatchState.Succeeded,
				parentFailed: parent.State == BatchState.Failed
			);
		}
	}

	private void SettleEdge(JobContinuationEdge edge, bool parentSucceeded, bool parentFailed)
	{
		if (!_jobs.TryGetValue(edge.ChildJobId, out var child) || IsTerminal(child.State))
			return;
		if (edge.Trigger == ContinuationTrigger.Success && !parentSucceeded)
		{
			_jobs[child.Id] = child with { RemainingDependencies = 0 };
			TransitionToTerminal(child.Id, JobState.Cancelled, error: null, timeProvider.GetUtcNow());
			return;
		}

		var remaining = Math.Max(0, child.RemainingDependencies - 1);
		var failed = child.FailedDependencies + (parentFailed ? 1 : 0);
		if (remaining == 0 && edge.Trigger == ContinuationTrigger.Failure && failed == 0)
		{
			_jobs[child.Id] = child with { RemainingDependencies = 0, FailedDependencies = failed };
			TransitionToTerminal(child.Id, JobState.Cancelled, error: null, timeProvider.GetUtcNow());
			return;
		}

		_jobs[child.Id] = child with
		{
			State = remaining == 0 && child.State == JobState.AwaitingContinuation
				? GetReadyState(child)
				: child.State,
			RemainingDependencies = remaining,
			FailedDependencies = failed,
		};
	}

	private JobState GetReadyState(JobRecord job) =>
		job.DueAt <= timeProvider.GetUtcNow() ? JobState.Pending : JobState.Scheduled;

	private JobContinuationEdge[] GetUnsettledWaiters(string parentJobId) =>
	[
		.. _edges.Where(edge => edge.ParentJobId == parentJobId &&
			!_settledEdges.Contains(edge) &&
			_jobs.TryGetValue(edge.ChildJobId, out var child) &&
			!IsTerminal(child.State)),
	];

	private void SpliceBeforeWaiters(
		string newParentJobId,
		IReadOnlyList<JobContinuationEdge> existingWaiters
	)
	{
		foreach (var existingEdge in existingWaiters)
		{
			if (!_jobs.TryGetValue(existingEdge.ChildJobId, out var child) || IsTerminal(child.State))
				continue;

			_jobs[child.Id] = child with
			{
				State = JobState.AwaitingContinuation,
				RemainingDependencies = child.RemainingDependencies + 1,
			};
			_edges.Add(new()
			{
				ChildJobId = child.Id,
				ParentJobId = newParentJobId,
				Trigger = existingEdge.Trigger,
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
				throw new ImmediateJobException($"Continuation dependency count overflow for job '{child.Id}'.");
			}
		}
	}

	private void IncrementBatchMembers(string? batchId, int count)
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

	private void MarkBatchStarted(string? batchId, DateTimeOffset startedAt)
	{
		if (batchId is not null && _batches.TryGetValue(batchId, out var batch) && batch.StartedAt is null)
			_batches[batchId] = batch with { StartedAt = startedAt };
	}

	private static bool IsTerminal(JobState state) =>
		state is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

	private static bool IsTerminal(BatchState state) => state is not BatchState.Executing;

	private static BatchStatus ToStatus(JobBatchRecord batch) => new(
		batch.Id,
		batch.State,
		batch.TotalJobs,
		batch.SucceededCount,
		batch.FailedCount,
		batch.CancelledCount,
		batch.PendingCount,
		batch.CreatedAt,
		batch.StartedAt,
		batch.CompletedAt,
		batch.TotalJobs == 0 ? 0 : (double)(batch.TotalJobs - batch.PendingCount) / batch.TotalJobs
	);

	private static BatchGraphEdge ToGraphEdge(JobContinuationEdge edge) => new(
		edge.ChildJobId,
		edge.ParentJobId,
		edge.ParentBatchId,
		edge.Trigger
	);

	private void RemoveEdgesForJobs(
		HashSet<string> jobIds,
		HashSet<string>? batchIds = null
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

	private JobRecord GetOwnedActive(string jobId, string workerId)
	{
		if (!_jobs.TryGetValue(jobId, out var job) || job.State != JobState.Active || job.WorkerId != workerId)
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

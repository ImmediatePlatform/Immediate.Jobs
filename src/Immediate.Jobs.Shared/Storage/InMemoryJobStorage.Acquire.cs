using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		RenewLeaseAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			ref var job = ref GetOwnedActive(jobHandle, executionNumber, workerId);
			job = job with { LeaseExpiresAt = timeProvider.GetUtcNow() + lease };
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		AcquireDueJobsAsyncCalled(request.WorkerId, request.BatchSize, request.Queues.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var now = timeProvider.GetUtcNow();
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

				var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
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

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private List<JobRecord> AcquireInExistingOrder(JobAcquisitionRequest request, DateTimeOffset now)
	{
		var acquired = new List<JobRecord>(request.BatchSize);
		foreach (var queue in request.Queues)
		{
			var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
			if (queueCapacity <= 0)
				continue;

			var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
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

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void AcquireQueueInExistingOrder(
		JobAcquisitionRequest request,
		string queueName,
		Dictionary<string, int> jobCapacities,
		int queueCapacity,
		DateTimeOffset now,
		List<JobRecord> acquired
	)
	{
		var eligibility = new Dictionary<string, bool>(StringComparer.Ordinal);
		foreach (var candidate in GetJobsForQueue(queueName, jobCapacities.Keys)
			.Where(job => job.State is JobState.Pending or JobState.Scheduled &&
				job.DueAt <= now)
			.OrderBy(job => job.DueAt)
			.ThenBy(job => job.CreatedAt)
			.ThenBy(job => job.JobHandle.Value, StringComparer.Ordinal))
		{
			if (queueCapacity == 0)
				break;
			if (jobCapacities[candidate.JobName] <= 0 || !CanAcquire(candidate.JobName, now, eligibility))
				continue;

			acquired.Add(Acquire(candidate, request, now));
			eligibility.Remove(candidate.JobName);
			jobCapacities[candidate.JobName]--;
			queueCapacity--;
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private bool HasEligibleGroupedJob(
		string queueName,
		Dictionary<string, int> jobCapacities,
		DateTimeOffset now
	) =>
		GetJobsForQueue(queueName, jobCapacities.Keys).Any(job => job.GroupId is not null &&
			jobCapacities.TryGetValue(job.JobName, out var capacity) &&
			capacity > 0 &&
			job.State is JobState.Pending or JobState.Scheduled &&
			job.DueAt <= now);

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
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
		// The gate and acquisition timestamp stay fixed for the entire batch. Build ordered
		// backlogs once, then advance past definitions whose capacity or limits are exhausted.
		var groups = GetJobsForQueue(queueName, jobCapacities.Keys)
			.Where(job => jobCapacities.TryGetValue(job.JobName, out var capacity) && capacity > 0 &&
				job.State is JobState.Pending or JobState.Scheduled && job.DueAt <= now)
			.OrderBy(static job => job.DueAt)
			.ThenBy(static job => job.CreatedAt)
			.ThenBy(static job => job.JobHandle.Value, StringComparer.Ordinal)
			.GroupBy(static job => job.GroupId, StringComparer.Ordinal)
			.Select(static group => new Queue<JobRecord>(group))
			.ToList();
		var eligibility = new Dictionary<string, bool>(StringComparer.Ordinal);
		var activeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		var totalActive = 0;
		foreach (var job in GetJobsForQueue(queueName))
		{
			if (job.State != JobState.Active || job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now)
			{
				continue;
			}

			totalActive++;
			if (job.GroupId is { } groupId)
				activeCounts[groupId] = activeCounts.GetValueOrDefault(groupId) + 1;
		}

		var nextSequence = policy.GroupRoundRobin ? GetNextSequence(queueName) : 0;
		var candidates = new List<Queue<JobRecord>>(groups.Count);
		while (queueCapacity > 0)
		{
			candidates.Clear();
			foreach (var group in groups)
			{
				while (group.TryPeek(out var head))
				{
					if (jobCapacities[head.JobName] > 0 && CanAcquire(head.JobName, now, eligibility))
					{
						candidates.Add(group);
						break;
					}

					group.Dequeue();
				}
			}

			if (candidates.Count == 0)
				break;

			var selected = candidates
				.OrderBy(group => IsNoisy(group.Peek().GroupId, activeCounts, totalActive, policy))
				.ThenBy(group => GetNoisyInflight(group.Peek().GroupId, activeCounts, totalActive, policy))
				.ThenBy(group => policy.GroupRoundRobin ? GetLastServed(queueName, group.Peek().GroupId) : 0)
				.ThenBy(static group => group.Peek().DueAt)
				.ThenBy(static group => group.Peek().CreatedAt)
				.ThenBy(static group => group.Peek().JobHandle.Value, StringComparer.Ordinal)
				.First();

			var job = Acquire(selected.Dequeue(), request, now);
			acquired.Add(job);
			eligibility.Remove(job.JobName);
			jobCapacities[job.JobName]--;
			queueCapacity--;
			if (job.LeaseExpiresAt > now)
			{
				totalActive++;
				if (job.GroupId is { } activeGroup)
					activeCounts[activeGroup] = activeCounts.GetValueOrDefault(activeGroup) + 1;
			}

			if (policy.GroupRoundRobin && job.GroupId is { } groupId)
				_fairQueueLastServed[(queueName, groupId)] = nextSequence++;
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private bool CanAcquire(string jobName, DateTimeOffset now, Dictionary<string, bool> eligibility)
	{
		if (!eligibility.TryGetValue(jobName, out var ready))
			eligibility.Add(jobName, ready = CanAcquire(jobName, now));
		return ready;
	}

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock; this method does not access mutable storage state.
	/// </remarks>
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

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock; this method does not access mutable storage state.
	/// </remarks>
	private static int GetNoisyInflight(
		string? groupId,
		Dictionary<string, int> activeCounts,
		int totalActive,
		FairQueuePolicy policy
	)
	{
		return IsNoisy(groupId, activeCounts, totalActive, policy) ? activeCounts[groupId!] : 0;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private long GetLastServed(string queueName, string? groupId)
	{
		return groupId is not null && _fairQueueLastServed.TryGetValue((queueName, groupId), out var sequence)
			? sequence
			: 0;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private long GetNextSequence(string queueName)
	{
		return _fairQueueLastServed
			.Where(pair => string.Equals(pair.Key.QueueName, queueName, StringComparison.Ordinal))
			.Select(static pair => pair.Value)
			.DefaultIfEmpty()
			.Max() + 1;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
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
		RecordAcquisition(job.JobName, now);
		return ReadRecord(job);
	}

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock; this method acquires it internally.
	/// </remarks>
	internal void ApplyAcquiredJobs(IReadOnlyList<JobRecord> acquired)
	{
		lock (_gate)
		{
			foreach (var job in acquired)
			{
				if (!_jobs.TryGetValue(job.JobHandle, out var previous) || job.Attempt != previous.Attempt + 1)
					throw new ImmediateJobException("The single-server primary has drifted from durable storage. Use only one scheduler process.");
				if (previous.State == JobState.Active)
					InterruptExecution(previous);
				else
					MaterializeSyntheticExecution(previous);
				SetJob(job with { QueueName = null });
				CreateExecution(job, timeProvider.GetUtcNow());
				MarkBatchStarted(job.BatchHandle, timeProvider.GetUtcNow());
			}
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void MarkBatchStarted(BatchHandle? batchHandle, DateTimeOffset startedAt)
	{
		if (batchHandle is null)
			return;

		ref var batch = ref ValidateExistingElement(_batches, batchHandle);

		if (batch.StartedAt is null)
			batch = batch with { StartedAt = startedAt };
	}

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryRenewLeaseAsyncCalled,
		EventName = "Immediate.Jobs.Shared.RenewLeaseAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RenewLeaseAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void RenewLeaseAsyncCalled(JobHandle jobHandle, int execution);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryAcquireDueJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.AcquireDueJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AcquireDueJobsAsync called (Worker={Worker}, BatchSize={BatchSize}, Queues={Queues})"
	)]
	private partial void AcquireDueJobsAsyncCalled(string worker, int batchSize, int queues);
}

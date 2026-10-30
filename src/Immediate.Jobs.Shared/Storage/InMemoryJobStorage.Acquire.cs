using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
			// only need to validate it exists
			GetOwnedActive(jobHandle, executionNumber, workerId);
		}
	}

	/// <summary>
	///		An <see langword="internal" /> to support use as primary storage for <see cref="SingleServerJobStorage"/>;
	///		updates the internal lease to match the durable storage.
	/// </summary>
	internal void ApplyRenewedLease(JobHandle jobHandle, int executionNumber, string workerId, TimeSpan lease)
	{
		lock (_gate)
		{
			ref var job = ref GetOwnedActive(jobHandle, executionNumber, workerId);
			UpdateJob(ref job, job with { LeaseExpiresAt = timeProvider.GetUtcNow() + lease });
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
			var selected = SelectDueJobs(request, now, useExpiringLeases: false);
			return AcquireJobs(selected, request.WorkerId, now, leaseExpiresAt: null, request.FairQueues?.GroupRoundRobin == true, reclaimExpired: false);
		}
	}

	/// <summary>
	/// 	Selects the jobs a request would acquire without changing ownership, history, limits, or fairness cursors.
	/// </summary>
	internal async ValueTask<IReadOnlyList<JobHandle>> ProvisionallyAcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
			return SelectDueJobs(request, timeProvider.GetUtcNow(), useExpiringLeases: true);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock. Only request-local selection state is modified.
	/// </remarks>
	private List<JobHandle> SelectDueJobs(JobAcquisitionRequest request, DateTimeOffset now, bool useExpiringLeases)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(request.BatchSize, nameof(request));
		if (request.BatchSize == 0 || (_runnableJobs.Count == 0 &&
			(!useExpiringLeases || _activeLeases.Count == 0 || _activeLeases.Min.Deadline > now)))
		{
			return [];
		}

		var selection = new AcquisitionSelection(this, now, request, useExpiringLeases);
		foreach (var queue in request.Queues)
		{
			var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - selection.Jobs.Count);
			if (queueCapacity <= 0)
				continue;

			var capacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
			if (request.FairQueues is { } policy && selection.GetJobsForQueue(queue.QueueName, capacities.Keys)
				.Any(job => job.GroupId is not null && capacities[job.JobName] > 0 && !selection.Contains(job.JobHandle)))
			{
				SelectQueueFairly(queue.QueueName, capacities, queueCapacity, policy, selection);
			}
			else
			{
				SelectQueueInExistingOrder(queue.QueueName, capacities, queueCapacity, now, selection);
			}
		}

		return selection.Jobs;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void SelectQueueInExistingOrder(
		string queueName,
		Dictionary<string, int> capacities,
		int queueCapacity,
		DateTimeOffset now,
		AcquisitionSelection selection
	)
	{
		var candidates = new PriorityQueue<int, RunnableJob>(capacities.Count);
		var iterators = new List<IEnumerator<RunnableJob>>(capacities.Count);
		try
		{
			foreach (var (name, capacity) in capacities)
			{
				if (capacity <= 0 || !string.Equals(GetQueueName(name), queueName, StringComparison.Ordinal)
					|| !selection.HasDueJobs(name) || !selection.CanAcquire(name))
				{
					continue;
				}

				foreach (var source in selection.GetSources(name))
				{
					var cursor = source.GetEnumerator();
					iterators.Add(cursor);
					if (AdvanceCandidate(cursor, now, selection))
						candidates.Enqueue(iterators.Count - 1, cursor.Current);
				}
			}

			while (queueCapacity > 0 && candidates.TryDequeue(out var index, out var head))
			{
				var iterator = iterators[index];
				var job = _jobs[head.JobHandle];
				if (capacities[job.JobName] <= 0 || !selection.CanAcquire(job.JobName))
					continue;
				selection.Select(job);
				capacities[job.JobName]--;
				queueCapacity--;

				if (queueCapacity > 0 && capacities[job.JobName] > 0 && selection.CanAcquire(job.JobName)
					&& AdvanceCandidate(iterator, now, selection))
				{
					candidates.Enqueue(index, iterator.Current);
				}
			}
		}
		finally
		{
			foreach (var iterator in iterators)
				iterator.Dispose();
		}
	}

	private static bool AdvanceCandidate(IEnumerator<RunnableJob> iterator, DateTimeOffset now, AcquisitionSelection selection)
	{
		while (iterator.MoveNext())
		{
			if (iterator.Current.DueAt > now)
				return false;
			if (!selection.Contains(iterator.Current.JobHandle))
				return true;
		}

		return false;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void SelectQueueFairly(
		string queueName,
		Dictionary<string, int> capacities,
		int queueCapacity,
		FairQueuePolicy policy,
		AcquisitionSelection selection
	)
	{
		var groups = selection.GetJobsForQueue(queueName, capacities.Keys)
			.Where(job => capacities[job.JobName] > 0 && !selection.Contains(job.JobHandle))
			.OrderBy(static job => job.DueAt)
			.ThenBy(static job => job.CreatedAt)
			.ThenBy(static job => job.JobHandle.Value, StringComparer.Ordinal)
			.GroupBy(static job => job.GroupId, StringComparer.Ordinal)
			.Select(static group => new Queue<JobRecord>(group))
			.ToList();
		var activeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		var totalActive = 0;
		foreach (var job in GetJobsForQueue(queueName))
		{
			if (job.State != JobState.Active || selection.IsExpired(job))
				continue;

			totalActive++;
			if (job.GroupId is { } groupId)
				activeCounts[groupId] = activeCounts.GetValueOrDefault(groupId) + 1;
		}

		foreach (var handle in selection.Jobs)
		{
			var job = _jobs[handle];
			if (!string.Equals(GetQueueName(job.JobName), queueName, StringComparison.Ordinal))
				continue;

			totalActive++;
			if (job.GroupId is { } groupId)
				activeCounts[groupId] = activeCounts.GetValueOrDefault(groupId) + 1;
		}

		var candidates = new List<Queue<JobRecord>>(groups.Count);
		while (queueCapacity > 0)
		{
			candidates.Clear();
			foreach (var group in groups)
			{
				while (group.TryPeek(out var head))
				{
					if (capacities[head.JobName] > 0 && selection.CanAcquire(head.JobName))
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
				.ThenBy(group => policy.GroupRoundRobin ? selection.GetLastServed(queueName, group.Peek().GroupId) : 0)
				.ThenBy(static group => group.Peek().DueAt)
				.ThenBy(static group => group.Peek().CreatedAt)
				.ThenBy(static group => group.Peek().JobHandle.Value, StringComparer.Ordinal)
				.First();

			var job = selected.Dequeue();
			selection.Select(job);
			capacities[job.JobName]--;
			queueCapacity--;
			totalActive++;
			if (job.GroupId is { } activeGroup)
				activeCounts[activeGroup] = activeCounts.GetValueOrDefault(activeGroup) + 1;
			if (policy.GroupRoundRobin && job.GroupId is { } groupId)
				selection.RecordGroup(queueName, groupId);
		}
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
	private JobRecord Acquire(ref JobRecord candidate, string workerId, DateTimeOffset now, DateTimeOffset? leaseExpiresAt)
	{
		MaterializeSyntheticExecution(candidate);

		var job = candidate with
		{
			State = JobState.Active,
			Attempt = candidate.Attempt + 1,
			WorkerId = workerId,
			LeaseExpiresAt = leaseExpiresAt,
			ExecutionTraceId = null,
			ExecutionSpanId = null,
			ExecutionStartedAt = null,
		};

		UpdateJob(ref candidate, job);
		CreateExecution(job, now);
		MarkBatchStarted(job.BatchHandle, now);
		RecordAcquisition(job.JobName, now);
		return ReadRecord(job);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock. Exact claims have already been selected or confirmed by durable storage.
	/// </remarks>
	private List<JobRecord> AcquireJobs(
		IReadOnlyCollection<JobHandle> jobs,
		string workerId,
		DateTimeOffset now,
		DateTimeOffset? leaseExpiresAt,
		bool roundRobin,
		bool reclaimExpired
	)
	{
		var acquired = new List<JobRecord>(jobs.Count);
		foreach (var handle in jobs)
		{
			ref var job = ref CollectionsMarshal.GetValueRefOrNullRef(_jobs, handle);
			if (Unsafe.IsNullRef(ref job))
				continue;
			var expired = reclaimExpired && job.State == JobState.Active && job.LeaseExpiresAt <= now;
			if (!expired && (job.State is not (JobState.Pending or JobState.Scheduled) || job.DueAt > now))
				continue;
			if (expired)
				CompleteExecution(job, JobExecutionState.Interrupted, job.LeaseExpiresAt!.Value);

			var record = Acquire(ref job, workerId, now, leaseExpiresAt);
			acquired.Add(record);
		}

		if (roundRobin)
			RecordFairAcquisitions(acquired);
		return acquired;
	}

	/// <summary>
	/// 	Advances round-robin cursors only for confirmed claims made under a round-robin policy.
	/// </summary>
	internal void ApplyFairQueueAcquisitions(IReadOnlyList<JobRecord> acquired, FairQueuePolicy? policy)
	{
		if (policy?.GroupRoundRobin != true || acquired.Count == 0)
			return;

		lock (_gate)
			RecordFairAcquisitions(acquired);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void RecordFairAcquisitions(IReadOnlyList<JobRecord> acquired)
	{
		var nextSequences = new Dictionary<string, long>(StringComparer.Ordinal);
		foreach (var job in acquired)
		{
			if (job.GroupId is { } groupId && job.QueueName is { } queueName)
				RecordGroup(_fairQueueLastServed, nextSequences, queueName, groupId);
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

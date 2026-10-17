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
			var job = GetOwnedActive(jobHandle, executionNumber, workerId);
			_jobs[jobHandle] = job with { LeaseExpiresAt = timeProvider.GetUtcNow() + lease };
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

	private IEnumerable<JobRecord> GetAcquisitionCandidates(Dictionary<string, int> jobCapacities) =>
		// A broad request benefits from one contiguous pass rather than resolving every stored handle.
		jobCapacities.Count >= _jobsByDefinition.Count
			? _jobs.Values
			: jobCapacities.Keys.SelectMany(GetJobsForDefinition);

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
		foreach (var candidate in GetAcquisitionCandidates(jobCapacities)
			.Where(job => string.Equals(GetQueueName(job.JobName), queueName, StringComparison.Ordinal) &&
				jobCapacities.ContainsKey(job.JobName) &&
				job.State is JobState.Pending or JobState.Scheduled &&
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

	private bool HasEligibleGroupedJob(
		string queueName,
		Dictionary<string, int> jobCapacities,
		DateTimeOffset now
	) =>
		GetAcquisitionCandidates(jobCapacities).Any(job => string.Equals(GetQueueName(job.JobName), queueName, StringComparison.Ordinal) &&
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
		// The gate and acquisition timestamp stay fixed for the entire batch. Build ordered
		// backlogs once, then advance past definitions whose capacity or limits are exhausted.
		var groups = GetAcquisitionCandidates(jobCapacities)
			.Where(job => string.Equals(GetQueueName(job.JobName), queueName, StringComparison.Ordinal) &&
				jobCapacities.TryGetValue(job.JobName, out var capacity) && capacity > 0 &&
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
		foreach (var job in _jobs.Values)
		{
			if (!string.Equals(GetQueueName(job.JobName), queueName, StringComparison.Ordinal) ||
				job.State != JobState.Active || job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now)
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

	private bool CanAcquire(string jobName, DateTimeOffset now, Dictionary<string, bool> eligibility)
	{
		if (!eligibility.TryGetValue(jobName, out var ready))
			eligibility.Add(jobName, ready = CanAcquire(jobName, now));
		return ready;
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
		RecordAcquisition(job.JobName, now);
		return ReadRecord(job);
	}

	private void MarkBatchStarted(BatchHandle? batchHandle, DateTimeOffset startedAt)
	{
		if (batchHandle is not null && _batches.TryGetValue(batchHandle, out var batch) && batch.StartedAt is null)
			_batches[batchHandle] = batch with { StartedAt = startedAt };
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryRenewLeaseAsyncCalled,
		EventName = "Immediate.Jobs.Shared.RenewLeaseAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RenewLeaseAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void RenewLeaseAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryAcquireDueJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.AcquireDueJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AcquireDueJobsAsync called (Worker={Worker}, BatchSize={BatchSize}, Queues={Queues})"
	)]
	private partial void AcquireDueJobsAsyncCalled(string worker, int batchSize, int queues);
}

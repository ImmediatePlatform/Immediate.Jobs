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

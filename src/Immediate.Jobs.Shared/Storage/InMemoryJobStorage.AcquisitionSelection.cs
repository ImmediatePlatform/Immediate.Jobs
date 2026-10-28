using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	// The caller holds _gate throughout selection. Reservations belong only to this
	// request, so a failed durable claim cannot consume local limits or fairness turns.
	private sealed class AcquisitionSelection(InMemoryJobStorage storage, DateTimeOffset now, JobAcquisitionRequest request, bool useExpiringLeases)
	{
		private readonly HashSet<JobHandle>? _selected = request.Queues.Count > 1 ? [with(request.BatchSize)] : null;
		private readonly Dictionary<string, int> _remaining = [with(StringComparer.OrdinalIgnoreCase)];
		private readonly Dictionary<(string QueueName, string GroupId), long> _lastServed = request.FairQueues?.GroupRoundRobin == true
			? new(storage._fairQueueLastServed) : [];
		private readonly Dictionary<string, long> _nextSequences = [with(StringComparer.Ordinal)];
		private readonly Dictionary<string, List<RunnableJob>> _expired = useExpiringLeases ? storage.GetExpiredJobs(now) : [with(StringComparer.OrdinalIgnoreCase)];

		public List<JobHandle> Jobs { get; } = [with(request.BatchSize)];

		public bool Contains(JobHandle handle) => _selected?.Contains(handle) ?? false;

		public bool CanAcquire(string name)
		{
			if (!_remaining.TryGetValue(name, out var remaining))
			{
				remaining = storage.GetRemainingAcquisitionCapacity(name, now, _expired.GetValueOrDefault(name)?.Count ?? 0);
				_remaining.Add(name, remaining);
			}

			return remaining > 0;
		}

		public void Select(JobRecord job)
		{
			_selected?.Add(job.JobHandle);
			Jobs.Add(job.JobHandle);
			_remaining[job.JobName]--;
		}

		public long GetLastServed(string queueName, string? groupId) =>
			groupId is not null ? _lastServed.GetValueOrDefault((queueName, groupId)) : 0;

		public void RecordGroup(string queueName, string groupId) =>
			storage.RecordGroup(_lastServed, _nextSequences, queueName, groupId);

		public bool IsExpired(JobRecord job) => useExpiringLeases && job.State == JobState.Active && job.LeaseExpiresAt <= now;

		public bool HasDueJobs(string name) => _expired.ContainsKey(name) ||
			(storage._runnableJobs.TryGetValue(name, out var runnable) && runnable.Min.DueAt <= now);

		public IEnumerable<IEnumerable<RunnableJob>> GetSources(string name)
		{
			if (storage._runnableJobs.TryGetValue(name, out var runnable) && runnable.Min.DueAt <= now)
				yield return runnable;
			if (_expired.TryGetValue(name, out var expired))
				yield return expired;
		}

		public IEnumerable<JobRecord> GetJobsForQueue(string queueName, IEnumerable<string> names)
		{
			foreach (var name in names)
			{
				if (!string.Equals(storage.GetQueueName(name), queueName, StringComparison.Ordinal))
					continue;

				foreach (var source in GetSources(name))
				{
					foreach (var entry in source)
					{
						if (entry.DueAt > now)
							break;
						yield return storage._jobs[entry.JobHandle];
					}
				}
			}
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock. Expiry is observed without changing ownership or history.
	/// </remarks>
	private Dictionary<string, List<RunnableJob>> GetExpiredJobs(DateTimeOffset now)
	{
		var expired = new Dictionary<string, List<RunnableJob>>(StringComparer.OrdinalIgnoreCase);
		foreach (var lease in _activeLeases)
		{
			if (lease.Deadline > now)
				break;

			var job = _jobs[lease.JobHandle];
			if (!expired.TryGetValue(job.JobName, out var jobs))
				expired.Add(job.JobName, jobs = []);
			jobs.Add(new(job.DueAt, job.CreatedAt, job.JobHandle));
		}

		foreach (var jobs in expired.Values)
			jobs.Sort();
		return expired;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock. Stored acquisition state is only read.
	/// </remarks>
	private int GetRemainingAcquisitionCapacity(string name, DateTimeOffset now, int expiredCount)
	{
		var definition = GetStoredDefinition(name);
		_acquisitionDefinitions.TryGetValue(definition.Name, out var usage);
		if (usage?.DurableRateLimitUntil > now)
			return 0;

		var limits = definition.AcquisitionLimits;
		var acquisitions = limits.SlidingWindowPeriod is { } period && usage is not null
			? usage.Acquisitions.Where(acquired => acquired > now - period).Order().ToList() : [];
		var state = JobAcquisitionEvaluator.Evaluate(definition.Name, usage?.State.IsPaused ?? false, limits, now,
			acquisitions, usage?.FixedWindowStart, usage?.FixedWindowCount ?? 0, _activeJobCounts.GetValueOrDefault(definition.Name) - expiredCount);
		if (state.AcquisitionStatus != JobAcquisitionStatus.Ready)
			return 0;

		var remaining = int.MaxValue;
		if (limits.MaxConcurrency > 0)
			remaining = Math.Min(remaining, limits.MaxConcurrency - state.ActiveCount);
		if (limits.SlidingWindowMax > 0)
			remaining = Math.Min(remaining, limits.SlidingWindowMax - state.SlidingWindowCount);
		if (limits.FixedWindowMax > 0)
			remaining = Math.Min(remaining, limits.FixedWindowMax - state.FixedWindowCount);
		return remaining;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock. The supplied cursors may be request-local or committed.
	/// </remarks>
	private void RecordGroup(
		Dictionary<(string QueueName, string GroupId), long> cursors,
		Dictionary<string, long> nextSequences,
		string queueName,
		string groupId
	)
	{
		var next = nextSequences.TryGetValue(queueName, out var sequence) ? sequence : GetNextSequence(queueName);
		cursors[(queueName, groupId)] = next;
		nextSequences[queueName] = next + 1;
	}
}

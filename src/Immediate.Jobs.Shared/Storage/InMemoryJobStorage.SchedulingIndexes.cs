using System.Runtime.InteropServices;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	private readonly record struct RunnableJob(DateTimeOffset DueAt, DateTimeOffset CreatedAt, JobHandle JobHandle);
	private readonly record struct JobLease(DateTimeOffset ExpiresAt, JobHandle JobHandle);

	private static readonly IComparer<RunnableJob> RunnableJobComparer = Comparer<RunnableJob>.Create(static (left, right) =>
	{
		var order = left.DueAt.CompareTo(right.DueAt);
		if (order == 0)
			order = left.CreatedAt.CompareTo(right.CreatedAt);
		return order != 0 ? order : StringComparer.Ordinal.Compare(left.JobHandle.Value, right.JobHandle.Value);
	});

	private static readonly IComparer<JobLease> JobLeaseComparer = Comparer<JobLease>.Create(static (left, right) =>
	{
		var order = left.ExpiresAt.CompareTo(right.ExpiresAt);
		return order != 0 ? order : StringComparer.Ordinal.Compare(left.JobHandle.Value, right.JobHandle.Value);
	});

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and enumeration of the returned sequence.
	/// </remarks>
	private IEnumerable<JobRecord> GetRunnableJobsForQueue(string queueName, IEnumerable<string> definitionNames, DateTimeOffset now)
	{
		foreach (var name in definitionNames)
		{
			if (!string.Equals(GetQueueName(name), queueName, StringComparison.Ordinal) || !_runnableJobs.TryGetValue(name, out var runnable))
				continue;
			foreach (var entry in runnable)
			{
				if (entry.DueAt > now)
					break;
				yield return _jobs[entry.JobHandle];
			}
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock. The reference must remain valid throughout this call.
	/// </remarks>
	private void UpdateJob(ref JobRecord stored, JobRecord updated)
	{
		var wasRunnable = stored.State is JobState.Pending or JobState.Scheduled;
		var isRunnable = updated.State is JobState.Pending or JobState.Scheduled;
		var orderChanged = stored.DueAt != updated.DueAt || stored.CreatedAt != updated.CreatedAt;
		if (wasRunnable && (!isRunnable || orderChanged))
			RemoveRunnableJob(stored);
		if (isRunnable && (!wasRunnable || orderChanged))
			AddRunnableJob(updated);

		var wasLeased = stored.State == JobState.Active && stored.LeaseExpiresAt is not null;
		var isLeased = updated.State == JobState.Active && updated.LeaseExpiresAt is not null;
		if (wasLeased && (!isLeased || stored.LeaseExpiresAt != updated.LeaseExpiresAt))
			_jobLeases.Remove(new(stored.LeaseExpiresAt!.Value, stored.JobHandle));
		if (isLeased && (!wasLeased || stored.LeaseExpiresAt != updated.LeaseExpiresAt))
			_jobLeases.Add(new(updated.LeaseExpiresAt!.Value, updated.JobHandle));
		if (wasLeased != isLeased)
			ChangeActiveLeaseCount(stored.JobName, isLeased ? 1 : -1);

		stored = updated;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void AddSchedulingIndexes(JobRecord job)
	{
		if (job.State is JobState.Pending or JobState.Scheduled)
			AddRunnableJob(job);
		if (job.State == JobState.Active && job.LeaseExpiresAt is { } expiresAt)
		{
			_jobLeases.Add(new(expiresAt, job.JobHandle));
			ChangeActiveLeaseCount(job.JobName, 1);
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void RemoveSchedulingIndexes(JobRecord job)
	{
		if (job.State is JobState.Pending or JobState.Scheduled)
			RemoveRunnableJob(job);
		if (job.State == JobState.Active && job.LeaseExpiresAt is { } expiresAt)
		{
			_jobLeases.Remove(new(expiresAt, job.JobHandle));
			ChangeActiveLeaseCount(job.JobName, -1);
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void AddRunnableJob(JobRecord job)
	{
		ref var runnable = ref CollectionsMarshal.GetValueRefOrAddDefault(_runnableJobs, job.JobName, out _);
		runnable ??= [with(RunnableJobComparer)];
		runnable.Add(new(job.DueAt, job.CreatedAt, job.JobHandle));
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void RemoveRunnableJob(JobRecord job)
	{
		var runnable = _runnableJobs[job.JobName];
		runnable.Remove(new(job.DueAt, job.CreatedAt, job.JobHandle));
		if (runnable.Count == 0)
			_runnableJobs.Remove(job.JobName);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void ChangeActiveLeaseCount(string jobName, int change)
	{
		ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(_activeLeaseCounts, jobName, out _);
		count += change;
		if (count == 0)
			_activeLeaseCounts.Remove(jobName);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private int GetActiveLeaseCount(string jobName, DateTimeOffset now) =>
		_jobLeases.Count == 0 || _jobLeases.Min.ExpiresAt > now
			? _activeLeaseCounts.GetValueOrDefault(jobName)
			: GetJobsForDefinition(jobName).Count(job => job.State == JobState.Active && job.LeaseExpiresAt > now);
}

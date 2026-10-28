using System.Runtime.InteropServices;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	private readonly record struct ActiveLease(DateTimeOffset Deadline, JobHandle JobHandle)
	{
		public static IComparer<ActiveLease> Comparer { get; } = Comparer<ActiveLease>.Create(static (left, right) =>
		{
			var order = left.Deadline.CompareTo(right.Deadline);
			return order != 0 ? order : StringComparer.Ordinal.Compare(left.JobHandle.Value, right.JobHandle.Value);
		});
	}

	private readonly record struct RunnableJob(DateTimeOffset DueAt, DateTimeOffset CreatedAt, JobHandle JobHandle) : IComparable<RunnableJob>
	{
		public int CompareTo(RunnableJob other)
		{
			var order = DueAt.CompareTo(other.DueAt);
			if (order != 0)
				return order;

			order = CreatedAt.CompareTo(other.CreatedAt);
			if (order != 0)
				return order;

			return StringComparer.Ordinal.Compare(JobHandle.Value, other.JobHandle.Value);

		}

		public static bool operator <(RunnableJob left, RunnableJob right) => Comparer<RunnableJob>.Default.Compare(left, right) < 0;
		public static bool operator <=(RunnableJob left, RunnableJob right) => Comparer<RunnableJob>.Default.Compare(left, right) <= 0;
		public static bool operator >(RunnableJob left, RunnableJob right) => Comparer<RunnableJob>.Default.Compare(left, right) > 0;
		public static bool operator >=(RunnableJob left, RunnableJob right) => Comparer<RunnableJob>.Default.Compare(left, right) >= 0;
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

		var wasActive = stored.State == JobState.Active;
		var isActive = updated.State == JobState.Active;

		if (wasActive != isActive)
			ChangeActiveJobCount(stored.JobName, isActive ? 1 : -1);
		if (wasActive && stored.LeaseExpiresAt is { } oldDeadline && (!isActive || stored.LeaseExpiresAt != updated.LeaseExpiresAt))
			_activeLeases.Remove(new(oldDeadline, stored.JobHandle));
		if (isActive && updated.LeaseExpiresAt is { } newDeadline && (!wasActive || stored.LeaseExpiresAt != updated.LeaseExpiresAt))
			_activeLeases.Add(new(newDeadline, updated.JobHandle));

		stored = updated;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void AddSchedulingIndexes(JobRecord job)
	{
		if (job.State is JobState.Pending or JobState.Scheduled)
			AddRunnableJob(job);

		if (job.State == JobState.Active)
		{
			ChangeActiveJobCount(job.JobName, 1);
			if (job.LeaseExpiresAt is { } deadline)
				_activeLeases.Add(new(deadline, job.JobHandle));
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void RemoveSchedulingIndexes(JobRecord job)
	{
		if (job.State is JobState.Pending or JobState.Scheduled)
			RemoveRunnableJob(job);

		if (job.State == JobState.Active)
		{
			ChangeActiveJobCount(job.JobName, -1);
			if (job.LeaseExpiresAt is { } deadline)
				_activeLeases.Remove(new(deadline, job.JobHandle));
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void AddRunnableJob(JobRecord job)
	{
		ref var runnable = ref CollectionsMarshal.GetValueRefOrAddDefault(_runnableJobs, job.JobName, out _);
		runnable ??= [];
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
	private void ChangeActiveJobCount(string jobName, int change)
	{
		ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(_activeJobCounts, jobName, out _);
		count += change;
		if (count == 0)
			_activeJobCounts.Remove(jobName);
	}
}

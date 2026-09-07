using System.ComponentModel;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Scheduler liveness state shared with health checks and monitoring.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class JobSchedulerState
{
	private readonly JobWorkerSnapshot?[] _workers;
	private readonly Lock _loopGate = new();
	private JobLoopSnapshot _acquisition = new();
	private JobLoopSnapshot _leaseRenewal = new();
	private long _activeWorkers;
	private long _startedAtTicks = -1;
	private long _lastHeartbeatTicks = -1;

	/// <summary>Creates state for the configured scheduler worker pool.</summary>
	/// <param name="options">The scheduler options.</param>
	public JobSchedulerState(IOptions<ImmediateJobsOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_workers = new JobWorkerSnapshot?[options.Value.WorkerCount];
	}

	/// <summary>
	/// 	Timestamp at which the scheduler initialized.
	/// </summary>
	/// <value>
	/// 	The initialization timestamp, or <see langword="null"/> before the scheduler starts.
	/// </value>
	public DateTimeOffset? StartedAt =>
		Interlocked.Read(ref _startedAtTicks) is var ticks && ticks >= 0
		   ? new DateTimeOffset(ticks, TimeSpan.Zero)
		   : null;

	/// <summary>
	/// 	Timestamp of the latest successful scheduler iteration.
	/// </summary>
	/// <value>
	/// 	The latest heartbeat timestamp, or <see langword="null"/> before the first heartbeat.
	/// </value>
	public DateTimeOffset? LastHeartbeat =>
		Interlocked.Read(ref _lastHeartbeatTicks) is var ticks && ticks >= 0
		   ? new DateTimeOffset(ticks, TimeSpan.Zero)
		   : null;

	/// <summary>
	/// 	Number of invocations currently executing.
	/// </summary>
	/// <value>
	/// 	The current number of active workers.
	/// </value>
	public int ActiveWorkers => checked((int)Interlocked.Read(ref _activeWorkers));

	/// <summary>The current state of every worker in the scheduler.</summary>
	public IReadOnlyList<JobWorkerSnapshot> Workers =>
		[.. _workers.Select((worker, workerId) => Volatile.Read(ref _workers[workerId]) ?? new JobWorkerSnapshot { WorkerId = workerId })];

	/// <summary>The current acquisition-loop state.</summary>
	public JobLoopSnapshot Acquisition { get { lock (_loopGate) return _acquisition; } }

	/// <summary>The current lease-renewal-loop state.</summary>
	public JobLoopSnapshot LeaseRenewal { get { lock (_loopGate) return _leaseRenewal; } }

	internal void MarkStarted(DateTimeOffset timestamp) =>
		Interlocked.Exchange(ref _startedAtTicks, timestamp.UtcTicks);

	internal void MarkHeartbeat(DateTimeOffset timestamp) =>
		Interlocked.Exchange(ref _lastHeartbeatTicks, timestamp.UtcTicks);

	internal void IncrementActive() => Interlocked.Increment(ref _activeWorkers);
	internal void DecrementActive() => Interlocked.Decrement(ref _activeWorkers);

	internal void StartExecution(int workerId, JobRecord record, DateTimeOffset startedAt)
	{
		Volatile.Write(ref _workers[workerId], new JobWorkerSnapshot
		{
			WorkerId = workerId,
			JobHandle = record.JobHandle,
			Attempt = record.Attempt,
			StartedAt = startedAt,
		});
		IncrementActive();
	}

	internal void FinishExecution(int workerId)
	{
		Volatile.Write(ref _workers[workerId], null);
		DecrementActive();
	}

	internal void StartAcquisition(DateTimeOffset timestamp)
	{
		lock (_loopGate) _acquisition = _acquisition with { IsRunning = true, LastAttemptedAt = timestamp };
	}

	internal void FinishAcquisition(DateTimeOffset timestamp, bool succeeded)
	{
		lock (_loopGate) _acquisition = succeeded
			? _acquisition with { IsRunning = false, LastSucceededAt = timestamp, ConsecutiveFailures = 0 }
			: _acquisition with { IsRunning = false, LastFailedAt = timestamp, ConsecutiveFailures = _acquisition.ConsecutiveFailures + 1 };
	}

	internal void StopAcquisition()
	{
		lock (_loopGate) _acquisition = _acquisition with { IsRunning = false };
	}

	internal void StartLeaseRenewal(DateTimeOffset timestamp, int examined)
	{
		lock (_loopGate) _leaseRenewal = _leaseRenewal with { IsRunning = true, LastAttemptedAt = timestamp, ItemsExamined = examined, ItemsSucceeded = 0, ItemsFailed = 0 };
	}

	internal void FinishLeaseRenewal(DateTimeOffset timestamp, int succeeded, int failed)
	{
		lock (_loopGate) _leaseRenewal = failed == 0
			? _leaseRenewal with { IsRunning = false, LastSucceededAt = timestamp, ConsecutiveFailures = 0, ItemsSucceeded = succeeded, ItemsFailed = 0 }
			: _leaseRenewal with { IsRunning = false, LastFailedAt = timestamp, ConsecutiveFailures = _leaseRenewal.ConsecutiveFailures + 1, ItemsSucceeded = succeeded, ItemsFailed = failed };
	}

	internal void StopLeaseRenewal()
	{
		lock (_loopGate) _leaseRenewal = _leaseRenewal with { IsRunning = false };
	}
}

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Scheduler liveness state shared with health checks and monitoring.
/// </summary>
public sealed class JobSchedulerState
{
	private long _activeWorkers;

	/// <summary>
	/// 	UTC time at which the scheduler initialized.
	/// </summary>
	/// <value>
	/// 	The initialization timestamp, or <see langword="null"/> before the scheduler starts.
	/// </value>
	public DateTimeOffset? StartedAt { get; private set; }

	/// <summary>
	/// 	UTC time of the latest successful scheduler iteration.
	/// </summary>
	/// <value>
	/// 	The latest heartbeat timestamp, or <see langword="null"/> before the first heartbeat.
	/// </value>
	public DateTimeOffset? LastHeartbeat { get; private set; }

	/// <summary>
	/// 	Number of invocations currently executing.
	/// </summary>
	/// <value>
	/// 	The current number of active workers.
	/// </value>
	public int ActiveWorkers => checked((int)Interlocked.Read(ref _activeWorkers));

	internal bool CodeSchedulesAsserted { get; private set; }

	internal void MarkStarted(DateTimeOffset timestamp) => StartedAt = timestamp;
	internal void MarkHeartbeat(DateTimeOffset timestamp) => LastHeartbeat = timestamp;
	internal void MarkCodeSchedulesAsserted() => CodeSchedulesAsserted = true;
	internal void IncrementActive() => Interlocked.Increment(ref _activeWorkers);
	internal void DecrementActive() => Interlocked.Decrement(ref _activeWorkers);
}

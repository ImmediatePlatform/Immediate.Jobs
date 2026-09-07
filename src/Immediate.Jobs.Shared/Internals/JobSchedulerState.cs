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
}

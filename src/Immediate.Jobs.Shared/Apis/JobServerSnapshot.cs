namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A live scheduler-node heartbeat.
/// </summary>
public sealed record JobServerSnapshot
{
	/// <summary>
	/// 	The scheduler-node identifier.
	/// </summary>
	public required string WorkerId { get; init; }

	/// <summary>
	/// 	The UTC time of the latest scheduler heartbeat.
	/// </summary>
	public required DateTimeOffset LastHeartbeat { get; init; }

	/// <summary>
	/// 	The number of active workers on the node.
	/// </summary>
	public required int ActiveWorkers { get; init; }

	/// <summary>
	/// 	The maximum number of workers on the node.
	/// </summary>
	public required int MaxWorkers { get; init; }

	/// <summary>
	/// 	How long this scheduler may be silent before it is considered dead.
	/// </summary>
	public TimeSpan ServerTimeout { get; init; } = TimeSpan.FromMinutes(2);

	/// <summary>
	/// 	The current state of each worker in this scheduler node.
	/// </summary>
	public IReadOnlyList<JobWorkerSnapshot> Workers { get; init; } = [];

	/// <summary>The current state of the job-acquisition loop.</summary>
	public JobLoopSnapshot Acquisition { get; init; } = new();

	/// <summary>The current state of the lease-renewal loop.</summary>
	public JobLoopSnapshot LeaseRenewal { get; init; } = new();
}

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
}

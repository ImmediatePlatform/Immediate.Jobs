namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A live scheduler-node heartbeat.
/// </summary>
/// <param name="WorkerId">
/// 	The scheduler-node identifier.
/// </param>
/// <param name="LastHeartbeat">
/// 	The UTC time of the latest scheduler heartbeat.
/// </param>
/// <param name="ActiveWorkers">
/// 	The number of active workers on the node.
/// </param>
/// <param name="MaxWorkers">
/// 	The maximum number of workers on the node.
/// </param>
public sealed record JobServerSnapshot(
	string WorkerId,
	DateTimeOffset LastHeartbeat,
	int ActiveWorkers,
	int MaxWorkers
);

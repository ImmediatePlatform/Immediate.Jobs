using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Queue totals for monitoring and health endpoints.
/// </summary>
/// <param name="CapturedAt">
/// 	The UTC time at which the snapshot was captured.
/// </param>
/// <param name="Counts">
/// 	The total job count for each lifecycle state.
/// </param>
/// <param name="Recurring">
/// 	The recurring schedules in the snapshot.
/// </param>
/// <param name="Servers">
/// 	The scheduler-node heartbeats in the snapshot.
/// </param>
public sealed record JobMonitoringSnapshot(
	DateTimeOffset CapturedAt,
	IReadOnlyDictionary<JobState, long> Counts,
	IReadOnlyList<RecurringJobSchedule> Recurring,
	IReadOnlyList<JobServerSnapshot> Servers
)
{
	/// <summary>
	/// 	Capabilities implemented by the active storage provider.
	/// </summary>
	/// <value>
	/// 	The capabilities implemented by the active storage provider.
	/// </value>
	public StorageCapabilities Capabilities { get; init; } = StorageCapabilities.Queue;
}

using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Queue totals for monitoring and health endpoints.
/// </summary>
public sealed record JobMonitoringSnapshot
{
	/// <summary>
	/// 	The UTC time at which the snapshot was captured.
	/// </summary>
	public required DateTimeOffset CapturedAt { get; init; }

	/// <summary>
	/// 	The total job count for each lifecycle state.
	/// </summary>
	public required IReadOnlyDictionary<JobState, long> Counts { get; init; }

	/// <summary>
	/// 	The recurring schedules in the snapshot.
	/// </summary>
	public required IReadOnlyList<RecurringJobSchedule> Recurring { get; init; }

	/// <summary>
	/// 	The scheduler-node heartbeats in the snapshot.
	/// </summary>
	public required IReadOnlyList<JobServerSnapshot> Servers { get; init; }

	/// <summary>
	/// 	Capabilities implemented by the active storage provider.
	/// </summary>
	/// <value>
	/// 	The capabilities implemented by the active storage provider.
	/// </value>
	public StorageCapabilities Capabilities { get; init; } = StorageCapabilities.Queue;
}

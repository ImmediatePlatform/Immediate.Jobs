namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	A priority-ordered, node-local storage acquisition request.
/// </summary>
public sealed record JobAcquisitionRequest
{
	/// <summary>
	/// 	The identifier of the worker node taking ownership.
	/// </summary>
	public required string WorkerId { get; init; }

	/// <summary>
	/// 	The lease assigned to acquired records.
	/// </summary>
	public required TimeSpan Lease { get; init; }

	/// <summary>
	/// 	The maximum number of records to acquire.
	/// </summary>
	public required int BatchSize { get; init; }

	/// <summary>
	/// 	Queues in dispatch order, with their remaining capacities.
	/// </summary>
	public required IReadOnlyList<JobQueueAcquisition> Queues { get; init; }

	/// <summary>
	/// 	Fair queue policy for this acquisition, or <see langword="null"/> when fairness is disabled.
	/// </summary>
	public FairQueuePolicy? FairQueues { get; init; }
}

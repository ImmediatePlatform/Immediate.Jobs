namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Describes the remaining acquisition capacity for one queue.
/// </summary>
public sealed record JobQueueAcquisition
{
	/// <summary>
	/// 	The persisted queue name.
	/// </summary>
	/// <value>
	/// 	The persisted queue name.
	/// </value>
	public required string QueueName { get; init; }

	/// <summary>
	/// 	Maximum records to acquire from this queue.
	/// </summary>
	/// <value>
	/// 	The maximum number of records to acquire from the queue.
	/// </value>
	public required int Capacity { get; init; }

	/// <summary>
	/// 	Remaining acquisition capacity by stable job name.
	/// </summary>
	/// <value>
	/// 	The remaining acquisition capacity keyed by stable job name.
	/// </value>
	public required IReadOnlyDictionary<string, int> JobCapacities { get; init; }
}

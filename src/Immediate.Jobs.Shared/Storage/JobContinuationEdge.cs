namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	A durable dependency edge from a job or batch to a child job.
/// </summary>
public sealed record JobContinuationEdge
{
	/// <summary>
	/// 	The waiting child invocation.
	/// </summary>
	/// <value>
	/// 	The waiting child invocation identifier.
	/// </value>
	public required string ChildJobId { get; init; }
	/// <summary>
	/// 	The parent invocation for a job-to-job dependency.
	/// </summary>
	/// <value>
	/// 	The parent invocation identifier, or <see langword="null"/> for a batch-to-job dependency.
	/// </value>
	public string? ParentJobId { get; init; }
	/// <summary>
	/// 	The parent batch for a batch-to-job dependency.
	/// </summary>
	/// <value>
	/// 	The parent batch identifier, or <see langword="null"/> for a job-to-job dependency.
	/// </value>
	public string? ParentBatchId { get; init; }
	/// <summary>
	/// 	The condition under which the edge is satisfied.
	/// </summary>
	/// <value>
	/// 	The dependency trigger.
	/// </value>
	public ContinuationTrigger Trigger { get; init; } = ContinuationTrigger.Success;
}

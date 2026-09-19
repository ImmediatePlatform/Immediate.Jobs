namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A durable dependency edge from a job or batch to a child job.
/// </summary>
public sealed record JobContinuationEdge
{
	/// <summary>
	/// 	The waiting child invocation.
	/// </summary>
	public required JobHandle ChildJobHandle { get; init; }

	/// <summary>
	/// 	The parent invocation for a job-to-job dependency.
	/// </summary>
	public JobHandle? ParentJobHandle { get; init; }

	/// <summary>
	/// 	The parent batch for a batch-to-job dependency.
	/// </summary>
	public BatchHandle? ParentBatchHandle { get; init; }

	/// <summary>
	///		The amount of delay after the parent job or batch completes to schedule the child job.
	/// </summary>
	public required TimeSpan Delay { get; init; }

	/// <summary>
	/// 	The condition under which the edge is satisfied.
	/// </summary>
	public ContinuationTrigger Trigger { get; init; } = ContinuationTrigger.Success;
}

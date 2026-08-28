namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A batch dependency graph.
/// </summary>
public sealed record BatchGraph
{
	/// <summary>
	/// 	The opaque batch identifier.
	/// </summary>
	public required BatchHandle BatchHandle { get; init; }

	/// <summary>
	/// 	The job nodes in the graph.
	/// </summary>
	public required IReadOnlyList<BatchGraphNode> Nodes { get; init; }

	/// <summary>
	/// 	The dependency edges in the graph.
	/// </summary>
	public required IReadOnlyList<BatchGraphEdge> Edges { get; init; }
}

/// <summary>
/// 	A job node in a batch graph.
/// </summary>
public sealed record BatchGraphNode
{
	/// <summary>
	/// 	The invocation identifier.
	/// </summary>
	public required JobHandle JobHandle { get; init; }

	/// <summary>
	/// 	The stable job name.
	/// </summary>
	public required string JobName { get; init; }

	/// <summary>
	/// 	The current invocation state.
	/// </summary>
	public required JobState State { get; init; }
}

/// <summary>
/// 	A dependency edge in a batch graph.
/// </summary>
public sealed record BatchGraphEdge
{
	/// <summary>
	/// 	The waiting child invocation identifier.
	/// </summary>
	public required JobHandle ChildJobHandle { get; init; }

	/// <summary>
	/// 	The parent invocation identifier for a job-to-job dependency.
	/// </summary>
	public JobHandle? ParentJobHandle { get; init; }

	/// <summary>
	/// 	The parent batch identifier for a batch-to-job dependency.
	/// </summary>
	public BatchHandle? ParentBatchHandle { get; init; }

	/// <summary>
	///		The amount of delay after the parent job or batch completes to schedule the child job.
	/// </summary>
	public required TimeSpan Delay { get; init; }

	/// <summary>
	/// 	The condition under which the edge is satisfied.
	/// </summary>
	public required ContinuationTrigger Trigger { get; init; }
}

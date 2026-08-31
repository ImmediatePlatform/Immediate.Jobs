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
	public required IReadOnlyList<JobContinuationEdge> Edges { get; init; }
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

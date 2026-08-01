namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A batch dependency graph.
/// </summary>
/// <param name="BatchId">
/// 	The opaque batch identifier.
/// </param>
/// <param name="Nodes">
/// 	The job nodes in the graph.
/// </param>
/// <param name="Edges">
/// 	The dependency edges in the graph.
/// </param>
public sealed record BatchGraph(
	string BatchId,
	IReadOnlyList<BatchGraphNode> Nodes,
	IReadOnlyList<BatchGraphEdge> Edges
);

/// <summary>
/// 	A job node in a batch graph.
/// </summary>
/// <param name="JobId">
/// 	The invocation identifier.
/// </param>
/// <param name="JobName">
/// 	The stable job name.
/// </param>
/// <param name="State">
/// 	The current invocation state.
/// </param>
public sealed record BatchGraphNode(string JobId, string JobName, JobState State);

/// <summary>
/// 	A dependency edge in a batch graph.
/// </summary>
/// <param name="ChildJobId">
/// 	The waiting child invocation identifier.
/// </param>
/// <param name="ParentJobId">
/// 	The parent invocation identifier for a job-to-job dependency.
/// </param>
/// <param name="ParentBatchId">
/// 	The parent batch identifier for a batch-to-job dependency.
/// </param>
/// <param name="Trigger">
/// 	The condition under which the edge is satisfied.
/// </param>
public sealed record BatchGraphEdge(
	string ChildJobId,
	string? ParentJobId,
	string? ParentBatchId,
	ContinuationTrigger Trigger
);

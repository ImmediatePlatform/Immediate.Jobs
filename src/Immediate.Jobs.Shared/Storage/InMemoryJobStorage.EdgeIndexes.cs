using System.Runtime.InteropServices;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and enumeration of the returned edges.
	/// </remarks>
	private IReadOnlyList<JobContinuationEdge> GetOutgoingEdges(JobHandle parent) =>
		_edgesByParentJob.TryGetValue(parent, out var edges) ? edges : Array.Empty<JobContinuationEdge>();

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and enumeration of the returned edges.
	/// </remarks>
	private IReadOnlyList<JobContinuationEdge> GetOutgoingEdges(BatchHandle parent) =>
		_edgesByParentBatch.TryGetValue(parent, out var edges) ? edges : Array.Empty<JobContinuationEdge>();

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and enumeration of the returned edges.
	/// </remarks>
	private IReadOnlyList<JobContinuationEdge> GetIncomingEdges(JobHandle child) =>
		_edgesByChildJob.TryGetValue(child, out var edges) ? edges : Array.Empty<JobContinuationEdge>();

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void AddEdges(IReadOnlyList<JobContinuationEdge> edges)
	{
		_edges.EnsureCapacity(_edges.Count + edges.Count);
		foreach (var edge in edges)
			AddEdge(edge);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void AddEdge(JobContinuationEdge edge)
	{
		_edges.Add(edge);
		AddEdgeToIndex(_edgesByChildJob, edge.ChildJobHandle, edge);
		if (edge.ParentJobHandle is { } job)
			AddEdgeToIndex(_edgesByParentJob, job, edge);
		if (edge.ParentBatchHandle is { } batch)
			AddEdgeToIndex(_edgesByParentBatch, batch, edge);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock while modifying the index.
	/// </remarks>
	private static void AddEdgeToIndex<TKey>(Dictionary<TKey, List<JobContinuationEdge>> index, TKey key, JobContinuationEdge edge)
		where TKey : notnull
	{
		ref var edges = ref CollectionsMarshal.GetValueRefOrAddDefault(index, key, out _);
		edges ??= [];
		edges.Add(edge);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void RemoveEdgesForJobs(HashSet<JobHandle> jobHandles, HashSet<BatchHandle>? batchHandles = null)
	{
		if (_edges.Count == 0 || (jobHandles.Count == 0 && (batchHandles is null || batchHandles.Count == 0)))
			return;

		var removed = new HashSet<JobContinuationEdge>();
		foreach (var job in jobHandles)
		{
			removed.UnionWith(GetOutgoingEdges(job));
			removed.UnionWith(GetIncomingEdges(job));
		}

		if (batchHandles is not null)
		{
			foreach (var batch in batchHandles)
				removed.UnionWith(GetOutgoingEdges(batch));
		}

		if (removed.Count == 0)
			return;

		_edges.RemoveAll(removed.Contains);
		_settledEdges.ExceptWith(removed);
		// Filter each affected bucket once, avoiding repeated list shifts during bulk purge.
		RemoveEdgesFromIndex(_edgesByChildJob, removed.Select(static edge => edge.ChildJobHandle).Distinct(), removed);
		RemoveEdgesFromIndex(_edgesByParentJob, removed.Where(static edge => edge.ParentJobHandle is not null)
			.Select(static edge => edge.ParentJobHandle!).Distinct(), removed);
		RemoveEdgesFromIndex(_edgesByParentBatch, removed.Where(static edge => edge.ParentBatchHandle is not null)
			.Select(static edge => edge.ParentBatchHandle!).Distinct(), removed);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock while modifying the index.
	/// </remarks>
	private static void RemoveEdgesFromIndex<TKey>(
		Dictionary<TKey, List<JobContinuationEdge>> index,
		IEnumerable<TKey> keys,
		HashSet<JobContinuationEdge> removed
	)
		where TKey : notnull
	{
		foreach (var key in keys)
		{
			var edges = index[key];
			edges.RemoveAll(removed.Contains);
			if (edges.Count == 0)
				index.Remove(key);
		}
	}
}

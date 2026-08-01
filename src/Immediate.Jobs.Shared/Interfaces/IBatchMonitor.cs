using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	Read-only batch monitoring.
/// </summary>
public interface IBatchMonitor
{
	/// <summary>
	/// 	Gets aggregate batch progress.
	/// </summary>
	/// <param name="batchId">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The aggregate batch status, or <see langword="null"/> when the batch does not exist.
	/// </returns>
	ValueTask<BatchStatus?> GetStatusAsync(string batchId, CancellationToken cancellationToken = default);
	/// <summary>
	/// 	Queries batch members.
	/// </summary>
	/// <param name="batchId">
	/// 	The batch identifier.
	/// </param>
	/// <param name="query">
	/// 	The member filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The batch members matching the query.
	/// </returns>
	ValueTask<IReadOnlyList<BatchMemberStatus>> QueryMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Gets the persisted dependency graph.
	/// </summary>
	/// <param name="batchId">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The batch dependency graph, or <see langword="null"/> when the batch does not exist.
	/// </returns>
	ValueTask<BatchGraph?> GetGraphAsync(string batchId, CancellationToken cancellationToken = default);
}

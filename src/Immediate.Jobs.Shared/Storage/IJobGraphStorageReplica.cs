using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Durable graph-replica capability required by single-server startup recovery.
/// </summary>
public interface IJobGraphStorageReplica
{
	/// <summary>
	/// 	Gets incoming continuation edges so a single-server store can reconstruct standalone continuations.
	/// </summary>
	/// <param name="childJobs">The child invocation handles whose incoming edges should be returned.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The incoming continuation edges for the supplied child invocations.</returns>
	ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(
		IReadOnlyCollection<JobHandle> childJobs,
		CancellationToken cancellationToken = default
	);
}

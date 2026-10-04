using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	/// <remarks>
	/// 	Not supported: the in-memory provider cannot be used as a single-server durable store.
	/// </remarks>
	/// <exception cref="NotSupportedException">Always thrown.</exception>
	public ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<JobHandle> jobs,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	) =>
		throw new NotSupportedException(JobStorageCapabilityGuards.SingleServerReplicaNotSupportedMessage);

	/// <inheritdoc />
	/// <remarks>
	/// 	Not supported: the in-memory provider cannot be used as a single-server durable store.
	/// </remarks>
	/// <exception cref="NotSupportedException">Always thrown.</exception>
	public ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(
		IReadOnlyCollection<JobHandle> childJobs,
		CancellationToken cancellationToken = default
	) =>
		throw new NotSupportedException(JobStorageCapabilityGuards.SingleServerReplicaNotSupportedMessage);
}

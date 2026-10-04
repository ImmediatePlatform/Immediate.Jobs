using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	/// <inheritdoc />
	/// <remarks>
	/// 	Not supported: a single-server store cannot be used as another single-server store's durable store.
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
	/// 	Not supported: a single-server store cannot be used as another single-server store's durable store.
	/// </remarks>
	/// <exception cref="NotSupportedException">Always thrown.</exception>
	public ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(
		IReadOnlyCollection<JobHandle> childJobs,
		CancellationToken cancellationToken = default
	) =>
		throw new NotSupportedException(JobStorageCapabilityGuards.SingleServerReplicaNotSupportedMessage);
}

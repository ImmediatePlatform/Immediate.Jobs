using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	/// <remarks>
	/// 	Applies the exact claims confirmed by the single-server durable store, in the supplied order.
	/// 	Selection and durable storage enforce acquisition limits before these claims are applied.
	/// </remarks>
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<JobHandle> jobs,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var now = timeProvider.GetUtcNow();
			return AcquireJobs(jobs, workerId, now, now + lease, roundRobin: false, reclaimExpired: true);
		}
	}

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

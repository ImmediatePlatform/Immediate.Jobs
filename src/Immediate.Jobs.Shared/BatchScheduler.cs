using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Default scoped atomic-batch scheduler.
/// </summary>
/// <param name="storage">
/// 	The storage provider used to persist batch graphs.
/// </param>
/// <param name="timeProvider">
/// 	The clock used to timestamp batches and jobs.
/// </param>
/// <param name="idGenerator">
/// 	The generator used to create batch and job identifiers.
/// </param>
public sealed class BatchScheduler(
	IJobStorage storage,
	TimeProvider timeProvider,
	IIdGenerator idGenerator
) : IBatchScheduler
{
	/// <inheritdoc />
	public ValueTask CancelAsync(BatchHandle batchId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(batchId);

		return JobStorageCapabilityGuards.RequireGraph(storage).CancelBatchAsync(batchId, cancellationToken);
	}

	/// <inheritdoc />
	public Batch Begin()
	{
		return new Batch(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			parents: null,
			ContinuationTrigger.Success
		);
	}

	/// <inheritdoc />
	public Batch Begin(BatchHandle batchId, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(batchId);
		return new Batch(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			[batchId],
			on
		);
	}

	/// <inheritdoc />
	public Batch Begin(IReadOnlyList<BatchHandle> batchId, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(batchId);
		if (batchId is [])
			ArgumentException.Throw(nameof(batchId), "No parent batches were provided");

		return new Batch(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			batchId,
			on
		);
	}

	/// <inheritdoc />
	public async ValueTask<BatchHandle> RunAsync(
		Func<Batch, ValueTask> body,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(body);
		await using var batch = Begin();
		await body(batch).ConfigureAwait(false);
		return await batch.CommitAsync(cancellationToken).ConfigureAwait(false);
	}
}

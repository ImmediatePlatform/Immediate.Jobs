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
	public ValueTask CancelAsync(BatchHandle handle, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(handle);
		return JobStorageCapabilityGuards.RequireGraph(storage).CancelBatchAsync(handle.BatchId, cancellationToken);
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
	public Batch Begin(BatchHandle batch, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(batch);
		return new Batch(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			[batch],
			on
		);
	}

	/// <inheritdoc />
	public Batch Begin(IReadOnlyList<BatchHandle> batches, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(batches);
		if (batches is [])
			ArgumentException.Throw(nameof(batches), "No parent batches were provided");

		return new Batch(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			batches,
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

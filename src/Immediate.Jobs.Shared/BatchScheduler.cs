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
	public async ValueTask CancelAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(batchHandle);

		await JobStorageCapabilityGuards.RequireGraph(storage).CancelBatchAsync(batchHandle, cancellationToken);
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
	public Batch Begin(BatchHandle batchHandle, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(batchHandle);
		return new Batch(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			[batchHandle],
			on
		);
	}

	/// <inheritdoc />
	public Batch Begin(IReadOnlyList<BatchHandle> batchHandle, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(batchHandle);
		if (batchHandle is [])
			ArgumentException.Throw(nameof(batchHandle), "No parent batches were provided");

		return new Batch(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			batchHandle,
			on
		);
	}

	/// <inheritdoc />
	public async ValueTask<BatchHandle> RunAsync(
		Func<Batch, ValueTask> body,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(body);
		await using var batch = Begin();
		await body(batch);
		return await batch.CommitAsync(cancellationToken);
	}
}

namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	Creates atomic batches of typed generated jobs.
/// </summary>
public interface IBatchScheduler
{
	/// <summary>
	/// 	Cancels every non-terminal member of a committed batch.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The committed batch to cancel.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous cancellation.
	/// </returns>
	ValueTask CancelAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Releases a batch committed with <see cref="Batch.CommitWaitingForTriggerAsync"/>.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The waiting batch to release.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous trigger.
	/// </returns>
	/// <exception cref="ImmediateJobException">
	/// 	The batch is no longer waiting for a trigger.
	/// </exception>
	ValueTask TriggerAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Releases a batch committed with <see cref="Batch.CommitWaitingForTriggerAsync"/>, unless it has already
	/// 	been released, cancelled, or completed.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The waiting batch to release.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	<see langword="true"/> when this call released the batch; otherwise, <see langword="false"/>.
	/// </returns>
	ValueTask<bool> TryTriggerAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Begins an in-memory batch buffer.
	/// </summary>
	/// <returns>
	/// 	The new batch buffer.
	/// </returns>
	Batch Begin();

	/// <summary>
	/// 	Begins a follow-up batch whose root members wait for a prior batch.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The batch that must reach a terminal state before the follow-up roots are released.
	/// </param>
	/// <param name="on">
	/// 	The parent-batch outcome that releases the follow-up roots.
	/// </param>
	/// <returns>
	/// 	The new follow-up batch buffer.
	/// </returns>
	Batch Begin(BatchHandle batchHandle, ContinuationTrigger on = ContinuationTrigger.Success);

	/// <summary>
	/// 	Begins a follow-up batch whose root members wait for a prior batch.
	/// </summary>
	/// <param name="batchHandles">
	/// 	The batches that must reach a terminal state before the follow-up roots are released.
	/// </param>
	/// <param name="on">
	/// 	The parent-batch outcome that releases the follow-up roots.
	/// </param>
	/// <returns>
	/// 	The new follow-up batch buffer.
	/// </returns>
	Batch Begin(IReadOnlyList<BatchHandle> batchHandles, ContinuationTrigger on = ContinuationTrigger.Success);

	/// <summary>
	/// 	Runs a batch body and commits it when the body succeeds.
	/// </summary>
	/// <param name="body">
	/// 	The callback that adds jobs and dependencies to the batch.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the commit operation.
	/// </param>
	/// <returns>
	/// 	A handle for the committed batch.
	/// </returns>
	ValueTask<BatchHandle> RunAsync(
		Func<Batch, ValueTask> body,
		CancellationToken cancellationToken = default
	);
}

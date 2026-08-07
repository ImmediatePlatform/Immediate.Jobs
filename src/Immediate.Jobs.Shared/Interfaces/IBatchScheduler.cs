namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	Creates atomic batches of typed generated jobs.
/// </summary>
public interface IBatchScheduler
{
	/// <summary>
	/// 	Cancels every non-terminal member of a committed batch.
	/// </summary>
	/// <param name="handle">
	/// 	The committed batch to cancel.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous cancellation.
	/// </returns>
	ValueTask CancelAsync(BatchHandle handle, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("This scheduler does not support cancelling batches.");

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
	/// <param name="after">
	/// 	The batch that must reach a terminal state before the follow-up roots are released.
	/// </param>
	/// <param name="on">
	/// 	The parent-batch outcome that releases the follow-up roots.
	/// </param>
	/// <returns>
	/// 	The new follow-up batch buffer.
	/// </returns>
	Batch Begin(BatchHandle after, ContinuationTrigger on = ContinuationTrigger.Success);

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

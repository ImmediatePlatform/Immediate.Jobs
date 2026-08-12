using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Storage capability for atomic batches and continuation graphs.
/// </summary>
public interface IJobGraphStorage : IJobStorage
{
	/// <summary>
	/// 	Atomically inserts a child invocation and its continuation dependencies.
	/// </summary>
	/// <param name="job">
	/// 	The child invocation to insert.
	/// </param>
	/// <param name="edges">
	/// 	The continuation dependencies to insert.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous enqueue operation.
	/// </returns>
	ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Atomically inserts a batch header, all members, and all dependency edges.
	/// </summary>
	/// <param name="batch">
	/// 	The batch header to insert.
	/// </param>
	/// <param name="jobs">
	/// 	The batch members to insert.
	/// </param>
	/// <param name="edges">
	/// 	The dependency edges to insert.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous enqueue operation.
	/// </returns>
	ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Marks an active job successful and atomically flushes its gated dynamic continuations.
	/// </summary>
	/// <param name="jobId">
	/// 	The active invocation identifier.
	/// </param>
	/// <param name="executionNumber">
	/// 	The execution ordinal that fences the active owner.
	/// </param>
	/// <param name="workerId">
	/// 	The identifier of the worker that owns the invocation.
	/// </param>
	/// <param name="additions">
	/// 	The buffered continuations to commit.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous completion.
	/// </returns>
	ValueTask CompleteWithContinuationsAsync(
		string jobId,
		int executionNumber,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Immediately adds a concurrent member to the batch of a running job.
	/// </summary>
	/// <param name="currentJobId">
	/// 	The running invocation whose batch receives the new member.
	/// </param>
	/// <param name="executionNumber">
	/// 	The execution ordinal that fences the running invocation.
	/// </param>
	/// <param name="job">
	/// 	The new batch member to insert.
	/// </param>
	/// <param name="options">
	/// 	How the new invocation joins the current workflow.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous addition.
	/// </returns>
	ValueTask AddBatchJobAsync(
		string currentJobId,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Gets aggregate progress for one batch.
	/// </summary>
	/// <param name="batchId">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The aggregate batch status, or <see langword="null"/> when the batch does not exist.
	/// </returns>
	ValueTask<BatchStatus?> GetBatchStatusAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Queries batch headers for dashboard presentation.
	/// </summary>
	/// <param name="query">
	/// 	The batch filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The batches matching the query.
	/// </returns>
	ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Queries members of one batch.
	/// </summary>
	/// <param name="batchId">
	/// 	The batch identifier.
	/// </param>
	/// <param name="query">
	/// 	The member filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The batch members matching the query.
	/// </returns>
	ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Gets the durable dependency graph for one batch.
	/// </summary>
	/// <param name="batchId">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The batch dependency graph, or <see langword="null"/> when the batch does not exist.
	/// </returns>
	ValueTask<BatchGraph?> GetBatchGraphAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Cancels every non-terminal member of an executing batch.
	/// </summary>
	/// <param name="batchId">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous cancellation.
	/// </returns>
	ValueTask CancelBatchAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Deletes a terminal batch, all of its members, and all related edges.
	/// </summary>
	/// <param name="batchId">
	/// 	The terminal batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous deletion.
	/// </returns>
	ValueTask DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Deletes terminal batch history older than the supplied retention periods.
	/// </summary>
	/// <param name="batchSucceededRetention">
	/// 	The retention period for successful batches.
	/// </param>
	/// <param name="batchFailedRetention">
	/// 	The retention period for failed or cancelled batches.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous purge operation.
	/// </returns>
	ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	);
}

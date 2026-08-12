using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	Read-only monitoring for jobs, executions, batches, schedules, and scheduler nodes.
/// </summary>
public interface IJobMonitor
{
	/// <summary>
	/// 	Gets the aggregate monitoring snapshot.
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The current aggregate monitoring snapshot.
	/// </returns>
	ValueTask<JobMonitoringSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Returns jobs matching a monitoring query.
	/// </summary>
	/// <param name="query">
	/// 	The job filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The jobs matching the query.
	/// </returns>
	ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Returns retained executions for one job.
	/// </summary>
	/// <param name="query">
	/// 	The owning job, exact-ordinal filter, and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The retained executions matching the query.
	/// </returns>
	ValueTask<IReadOnlyList<JobExecutionRecord>> QueryExecutionsAsync(
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Gets one job and its incoming dependencies.
	/// </summary>
	/// <param name="job">
	/// 	The invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The job status, or <see langword="null"/> when the invocation does not exist.
	/// </returns>
	ValueTask<JobStatus?> GetJobAsync(JobHandle job, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Returns batches matching a monitoring query.
	/// </summary>
	/// <param name="query">
	/// 	The batch filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The batches matching the query, or <see langword="null"/> when the current job storage does not support batches.
	/// </returns>
	ValueTask<IReadOnlyList<BatchStatus>?> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Gets aggregate progress for one batch.
	/// </summary>
	/// <param name="batch">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The aggregate batch status, or <see langword="null"/> when the batch does not exist or the current job storage does not support batches.
	/// </returns>
	ValueTask<BatchStatus?> GetBatchAsync(BatchHandle batch, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Queries members of one batch.
	/// </summary>
	/// <param name="batch">
	/// 	The batch identifier.
	/// </param>
	/// <param name="query">
	/// 	The member filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The batch members matching the query, or <see langword="null"/> when the current job storage does not support batches.
	/// </returns>
	ValueTask<IReadOnlyList<BatchMemberStatus>?> QueryBatchMembersAsync(
		BatchHandle batch,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Gets the persisted dependency graph for one batch.
	/// </summary>
	/// <param name="batch">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The batch dependency graph, or <see langword="null"/> when the batch does not exist or the current job storage does not support batches.
	/// </returns>
	ValueTask<BatchGraph?> GetBatchGraphAsync(
		BatchHandle batch,
		CancellationToken cancellationToken = default
	);
}

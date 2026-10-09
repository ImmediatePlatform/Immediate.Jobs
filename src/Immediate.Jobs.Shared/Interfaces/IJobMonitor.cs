using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	Storage-backed monitoring and definition acquisition controls for jobs, executions, batches, schedules, and scheduler nodes.
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
	/// 	Gets stored job and recurring configuration without live acquisition state.
	/// </summary>
	/// <param name="cancellationToken">A token that can cancel the read.</param>
	/// <returns>All persisted monitoring definitions.</returns>
	ValueTask<JobMonitoringDefinitions> GetDefinitionsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Pauses acquisition of a stored definition's pending and future jobs. Running work may finish.
	/// </summary>
	/// <param name="jobName">
	/// 	The stable job definition name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	The asynchronous pause operation.
	/// </returns>
	/// <exception cref="KeyNotFoundException">
	/// 	The definition is not stored.
	/// </exception>
	ValueTask PauseDefinitionAsync(string jobName, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Resumes acquisition of a stored definition, preserving its rate counters and configured limits.
	/// </summary>
	/// <param name="jobName">
	/// 	The stable job definition name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	The asynchronous resume operation.
	/// </returns>
	/// <exception cref="KeyNotFoundException">
	/// 	The definition is not stored.
	/// </exception>
	ValueTask ResumeDefinitionAsync(string jobName, CancellationToken cancellationToken = default);

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
	/// <param name="jobHandle">
	/// 	The job identifier.
	/// </param>
	/// <param name="query">
	/// 	The exact-ordinal filter and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The retained executions matching the query.
	/// </returns>
	ValueTask<IReadOnlyList<JobExecutionRecord>> QueryExecutionsAsync(
		JobHandle jobHandle,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Gets one job and its incoming dependencies.
	/// </summary>
	/// <param name="jobHandle">
	/// 	The invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The job status, or <see langword="null"/> when the invocation does not exist.
	/// </returns>
	ValueTask<JobStatus?> GetJobAsync(JobHandle jobHandle, CancellationToken cancellationToken = default);

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
	/// <param name="batchHandle">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The aggregate batch status, or <see langword="null"/> when the batch does not exist or the current job storage does not support batches.
	/// </returns>
	ValueTask<BatchStatus?> GetBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Queries members of one batch.
	/// </summary>
	/// <param name="batchHandle">
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
		BatchHandle batchHandle,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Gets the persisted dependency graph for one batch.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The batch dependency graph, or <see langword="null"/> when the batch does not exist or the current job storage does not support batches.
	/// </returns>
	ValueTask<BatchGraph?> GetBatchGraphAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	);
}

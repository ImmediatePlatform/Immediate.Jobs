using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// The storage seam implemented by all job providers. Implementations must tolerate repeated
/// <see cref="IAsyncDisposable.DisposeAsync"/> calls because one instance may expose multiple storage capabilities.
/// 
/// </summary>
public interface IJobStorage : IAsyncDisposable
{
	/// <summary>
	/// 	Creates or upgrades provider storage.
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous initialization.
	/// </returns>
	ValueTask InitializeAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Inserts a pending or scheduled invocation.
	/// </summary>
	/// <param name="job">
	/// 	The invocation to insert.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous enqueue operation.
	/// </returns>
	ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Atomically claims due work in the requested queue and job-capacity order.
	/// </summary>
	/// <param name="request">
	/// 	The acquisition capacities and worker lease details.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The invocations acquired for the worker.
	/// </returns>
	ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Records OpenTelemetry correlation for the active execution attempt.
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
	/// <param name="traceId">
	/// 	The execution trace identifier, if one was created.
	/// </param>
	/// <param name="spanId">
	/// 	The execution span identifier, if one was created.
	/// </param>
	/// <param name="startedAt">
	/// 	The UTC time at which execution started.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous update.
	/// </returns>
	ValueTask SetExecutionTelemetryAsync(
		string jobId,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Extends the lease on an active job owned by the worker.
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
	/// <param name="lease">
	/// 	The new lease duration.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous lease renewal.
	/// </returns>
	ValueTask RenewLeaseAsync(
		string jobId,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Marks an active job successful.
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
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous completion.
	/// </returns>
	ValueTask CompleteAsync(
		string jobId,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Reschedules or dead-letters a failed attempt.
	/// </summary>
	/// <param name="jobId">
	/// 	The failed invocation identifier.
	/// </param>
	/// <param name="executionNumber">
	/// 	The execution ordinal that fences the active owner.
	/// </param>
	/// <param name="workerId">
	/// 	The identifier of the worker that owns the invocation.
	/// </param>
	/// <param name="error">
	/// 	The failure details to persist.
	/// </param>
	/// <param name="nextRetryAt">
	/// 	The next UTC retry time, or <see langword="null"/> to dead-letter the invocation.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous failure update.
	/// </returns>
	ValueTask FailAsync(
		string jobId,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Returns aggregate monitoring data.
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The current aggregate monitoring snapshot.
	/// </returns>
	ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Returns jobs matching a dashboard query.
	/// </summary>
	/// <param name="query">
	/// 	The job filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The jobs matching the query.
	/// </returns>
	ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Returns retained executions for one job, newest first unless an exact ordinal is requested.
	/// </summary>
	/// <param name="query">
	/// 	The owning job, exact-ordinal filter, and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The matching retained executions, or an empty list when the job does not exist.
	/// </returns>
	ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Gets one job and its incoming dependencies.
	/// </summary>
	/// <param name="jobId">
	/// 	The invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The job status, or <see langword="null"/> when the invocation does not exist.
	/// </returns>
	ValueTask<JobStatus?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>
	///		Moves a non-terminal invocation to the cancelled state.
	/// </summary>
	/// <param name="jobId">
	///		The non-terminal invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	///		A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	///		A value task that represents the asynchronous cancellation.
	/// </returns>
	ValueTask CancelAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Moves a failed invocation back to pending or fast-forwards a scheduled invocation.
	/// </summary>
	/// <param name="jobId">
	/// 	The failed or scheduled invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous retry operation.
	/// </returns>
	ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Deletes a terminal invocation.
	/// </summary>
	/// <param name="jobId">
	/// 	The terminal invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous deletion.
	/// </returns>
	ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Deletes terminal job history older than the supplied retention periods.
	/// </summary>
	/// <param name="succeededRetention">
	/// 	The retention period for successful invocations.
	/// </param>
	/// <param name="failedRetention">
	/// 	The retention period for failed, cancelled, or skipped invocations.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous purge operation.
	/// </returns>
	ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Records scheduler liveness for monitoring.
	/// </summary>
	/// <param name="server">
	/// 	The scheduler-node snapshot to persist.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous heartbeat update.
	/// </returns>
	ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Checks provider connectivity.
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns><see langword="true"/> when the provider is reachable; otherwise, <see langword="false"/>.
	/// </returns>
	ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}

namespace Immediate.Jobs.Shared;

/// <summary>
/// The storage seam implemented by all job providers. Implementations must tolerate repeated
/// <see cref="IAsyncDisposable.DisposeAsync"/> calls because one instance may expose multiple storage capabilities.
/// </summary>
public interface IJobStorage : IAsyncDisposable
{
	/// <summary>Creates or upgrades provider storage.</summary>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous initialization.</returns>
	ValueTask InitializeAsync(CancellationToken cancellationToken = default);

	/// <summary>Inserts a pending or scheduled invocation.</summary>
	/// <param name="job">The invocation to insert.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous enqueue operation.</returns>
	ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default);

	/// <summary>Atomically claims due work in the requested queue and job-capacity order.</summary>
	/// <param name="request">The acquisition capacities and worker lease details.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The invocations acquired for the worker.</returns>
	ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	);

	/// <summary>Records OpenTelemetry correlation for the active execution attempt.</summary>
	/// <param name="jobId">The active invocation identifier.</param>
	/// <param name="executionNumber">The execution ordinal that fences the active owner.</param>
	/// <param name="workerId">The identifier of the worker that owns the invocation.</param>
	/// <param name="traceId">The execution trace identifier, if one was created.</param>
	/// <param name="spanId">The execution span identifier, if one was created.</param>
	/// <param name="startedAt">The UTC time at which execution started.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous update.</returns>
	ValueTask SetExecutionTelemetryAsync(
		string jobId,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	);

	/// <summary>Extends the lease on an active job owned by the worker.</summary>
	/// <param name="jobId">The active invocation identifier.</param>
	/// <param name="executionNumber">The execution ordinal that fences the active owner.</param>
	/// <param name="workerId">The identifier of the worker that owns the invocation.</param>
	/// <param name="lease">The new lease duration.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous lease renewal.</returns>
	ValueTask RenewLeaseAsync(
		string jobId,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	);

	/// <summary>Marks an active job successful.</summary>
	/// <param name="jobId">The active invocation identifier.</param>
	/// <param name="executionNumber">The execution ordinal that fences the active owner.</param>
	/// <param name="workerId">The identifier of the worker that owns the invocation.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous completion.</returns>
	ValueTask CompleteAsync(
		string jobId,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	);

	/// <summary>Reschedules or dead-letters a failed attempt.</summary>
	/// <param name="jobId">The failed invocation identifier.</param>
	/// <param name="executionNumber">The execution ordinal that fences the active owner.</param>
	/// <param name="workerId">The identifier of the worker that owns the invocation.</param>
	/// <param name="error">The failure details to persist.</param>
	/// <param name="nextRetryAt">The next UTC retry time, or <see langword="null"/> to dead-letter the invocation.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous failure update.</returns>
	ValueTask FailAsync(
		string jobId,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	);

	/// <summary>Returns aggregate monitoring data.</summary>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The current aggregate monitoring snapshot.</returns>
	ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default);

	/// <summary>Returns jobs matching a dashboard query.</summary>
	/// <param name="query">The job filters and paging options.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The jobs matching the query.</returns>
	ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default);

	/// <summary>Returns retained executions for one job, newest first unless an exact ordinal is requested.</summary>
	/// <param name="query">The owning job, exact-ordinal filter, and paging options.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The matching retained executions.</returns>
	ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>Gets one job and its incoming dependencies.</summary>
	/// <param name="jobId">The invocation identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The job status, or <see langword="null"/> when the invocation does not exist.</returns>
	ValueTask<JobStatus?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>Moves a failed invocation back to pending or fast-forwards a scheduled invocation.</summary>
	/// <param name="jobId">The failed or scheduled invocation identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous retry operation.</returns>
	ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>Deletes a terminal invocation.</summary>
	/// <param name="jobId">The terminal invocation identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous deletion.</returns>
	ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>Deletes terminal job history older than the supplied retention periods.</summary>
	/// <param name="succeededRetention">The retention period for successful invocations.</param>
	/// <param name="failedRetention">The retention period for failed, cancelled, or skipped invocations.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous purge operation.</returns>
	ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	);

	/// <summary>Records scheduler liveness for monitoring.</summary>
	/// <param name="server">The scheduler-node snapshot to persist.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous heartbeat update.</returns>
	ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default);

	/// <summary>Checks provider connectivity.</summary>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns><see langword="true"/> when the provider is reachable; otherwise, <see langword="false"/>.</returns>
	ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}

/// <summary>Storage capability for recurring schedules and occurrence materialization.</summary>
public interface IRecurringJobStorage : IJobStorage
{
	/// <summary>Creates or updates a recurring schedule.</summary>
	/// <param name="schedule">The recurring schedule to create or update.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous upsert.</returns>
	ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default);

	/// <summary>Removes code-defined schedules that are not in the supplied active schedule names.</summary>
	/// <param name="activeScheduleNames">The names of code-defined schedules that remain active.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous removal.</returns>
	ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	);

	/// <summary>Removes a dynamic recurring schedule.</summary>
	/// <param name="name">The recurring schedule name.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous removal.</returns>
	ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>Pauses a recurring schedule.</summary>
	/// <param name="name">The recurring schedule name.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous pause operation.</returns>
	ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>Resumes a recurring schedule.</summary>
	/// <param name="name">The recurring schedule name.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous resume operation.</returns>
	ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>Returns schedules ready to materialize.</summary>
	/// <param name="now">The current UTC time used to determine which schedules are due.</param>
	/// <param name="batchSize">The maximum number of schedules to return.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The recurring schedules that are ready to materialize.</returns>
	ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	);

	/// <summary>Atomically creates a recurring invocation and advances the schedule.</summary>
	/// <param name="schedule">The recurring schedule being materialized.</param>
	/// <param name="job">The invocation to insert.</param>
	/// <param name="nextRunAt">The next UTC occurrence for the schedule.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns><see langword="true"/> when the occurrence was materialized; otherwise, <see langword="false"/>.</returns>
	ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	);
}

/// <summary>Storage capability for atomic batches and continuation graphs.</summary>
public interface IJobGraphStorage : IJobStorage
{
	/// <summary>
	/// Gets incoming continuation edges for the supplied child invocations. Implementations return an
	/// empty result for an empty collection and treat duplicate child identifiers as one lookup.
	/// </summary>
	/// <param name="childJobIds">Non-null child invocation identifiers. Identifiers cannot be null or blank.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The incoming continuation edges for the supplied child invocations.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="childJobIds"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException"><paramref name="childJobIds"/> contains a null or blank identifier.</exception>
	ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(
		IReadOnlyCollection<string> childJobIds,
		CancellationToken cancellationToken = default
	);

	/// <summary>Atomically inserts a child invocation and its continuation dependencies.</summary>
	/// <param name="job">The child invocation to insert.</param>
	/// <param name="edges">The continuation dependencies to insert.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous enqueue operation.</returns>
	ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	);

	/// <summary>Atomically inserts a batch header, all members, and all dependency edges.</summary>
	/// <param name="batch">The batch header to insert.</param>
	/// <param name="jobs">The batch members to insert.</param>
	/// <param name="edges">The dependency edges to insert.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous enqueue operation.</returns>
	ValueTask EnqueueBatchAsync(
		JobBatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	);

	/// <summary>Marks an active job successful and atomically flushes its gated dynamic continuations.</summary>
	/// <param name="jobId">The active invocation identifier.</param>
	/// <param name="executionNumber">The execution ordinal that fences the active owner.</param>
	/// <param name="workerId">The identifier of the worker that owns the invocation.</param>
	/// <param name="additions">The buffered continuations to commit.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous completion.</returns>
	ValueTask CompleteWithContinuationsAsync(
		string jobId,
		int executionNumber,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	);

	/// <summary>Immediately adds a concurrent member to the batch of a running job.</summary>
	/// <param name="currentJobId">The running invocation whose batch receives the new member.</param>
	/// <param name="executionNumber">The execution ordinal that fences the running invocation.</param>
	/// <param name="job">The new batch member to insert.</param>
	/// <param name="options">How the new invocation joins the current workflow.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous addition.</returns>
	ValueTask AddBatchJobAsync(
		string currentJobId,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	);

	/// <summary>Gets aggregate progress for one batch.</summary>
	/// <param name="batchId">The batch identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The aggregate batch status, or <see langword="null"/> when the batch does not exist.</returns>
	ValueTask<BatchStatus?> GetBatchStatusAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>Queries batch headers for dashboard presentation.</summary>
	/// <param name="query">The batch filters and paging options.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The batches matching the query.</returns>
	ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		JobBatchQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>Queries members of one batch.</summary>
	/// <param name="batchId">The batch identifier.</param>
	/// <param name="query">The member filters and paging options.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The batch members matching the query.</returns>
	ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>Gets the durable dependency graph for one batch.</summary>
	/// <param name="batchId">The batch identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The batch dependency graph, or <see langword="null"/> when the batch does not exist.</returns>
	ValueTask<BatchGraph?> GetBatchGraphAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>Cancels every non-terminal member of an executing batch.</summary>
	/// <param name="batchId">The batch identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous cancellation.</returns>
	ValueTask CancelBatchAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>Deletes a terminal batch, all of its members, and all related edges.</summary>
	/// <param name="batchId">The terminal batch identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous deletion.</returns>
	ValueTask DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>Deletes terminal batch history older than the supplied retention periods.</summary>
	/// <param name="batchSucceededRetention">The retention period for successful batches.</param>
	/// <param name="batchFailedRetention">The retention period for failed or cancelled batches.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>A value task that represents the asynchronous purge operation.</returns>
	ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	);
}

/// <summary>
/// Provider capability required by single-server mode to mirror the exact set of jobs selected by
/// its authoritative in-process queue. Custom providers only need this capability when used as a
/// single-server durable replica.
/// </summary>
public interface IJobStorageReplica
{
	/// <summary>Acquires the specified due invocations for the supplied worker.</summary>
	/// <param name="jobIds">The due invocation identifiers to acquire.</param>
	/// <param name="workerId">The identifier of the worker taking ownership.</param>
	/// <param name="lease">The lease duration assigned to acquired invocations.</param>
	/// <param name="cancellationToken">A token that can cancel the storage operation.</param>
	/// <returns>The invocations acquired for the worker.</returns>
	ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<string> jobIds,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	);
}

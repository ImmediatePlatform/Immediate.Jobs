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
	/// 	Atomically reconciles definitions and code-defined schedules against the complete application catalogue.
	/// </summary>
	/// <param name="registration">
	/// 	The complete local definition catalogue, and code-defined schedules.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	A value task representing the complete reconciliation.
	/// </returns>
	/// <remarks>
	/// 	Supplied definitions are inserted or updated; stored definitions omitted from the supplied list
	/// 	are removed. Dynamic schedules, and invocation history are preserved.
	/// 	Each server's list must be authoritative for the catalogue. Repeated calls are idempotent.
	/// </remarks>
	ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Returns every persisted definition, ordered by ordinal, case-insensitive name, without local registration filtering.
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	The complete stored definition catalogue.
	/// </returns>
	ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default);

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
	/// 	Atomically replaces the payload of an invocation that is waiting for a trigger.
	/// </summary>
	/// <param name="jobHandle">
	/// 	The invocation identifier.
	/// </param>
	/// <param name="expectedJobName">
	/// 	The job name the invocation must have.
	/// </param>
	/// <param name="payload">
	/// 	The serialized replacement payload.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous update.
	/// </returns>
	/// <exception cref="KeyNotFoundException">
	/// 	The invocation does not exist.
	/// </exception>
	/// <exception cref="ImmediateJobException">
	/// 	The invocation has a different job name or is not <see cref="JobState.WaitingForTrigger"/>.
	/// </exception>
	ValueTask UpdatePayloadAsync(
		JobHandle jobHandle,
		string expectedJobName,
		string payload,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Atomically releases an invocation that is waiting for a trigger.
	/// </summary>
	/// <param name="jobHandle">
	/// 	The invocation identifier.
	/// </param>
	/// <param name="expectedJobName">
	/// 	The job name the invocation must have.
	/// </param>
	/// <param name="dueAt">
	/// 	The UTC time at which the released invocation becomes due.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	<see langword="true"/> when this call released the invocation; <see langword="false"/> when it is no
	/// 	longer <see cref="JobState.WaitingForTrigger"/>.
	/// </returns>
	/// <exception cref="KeyNotFoundException">
	/// 	The invocation does not exist.
	/// </exception>
	/// <exception cref="ImmediateJobException">
	/// 	The invocation has a different job name, belongs to a batch that is waiting for a trigger, or is waiting without a payload.
	/// </exception>
	/// <remarks>
	/// 	A released invocation with unsettled dependencies becomes <see cref="JobState.AwaitingContinuation"/>.
	/// 	Otherwise it becomes <see cref="JobState.Pending"/> or <see cref="JobState.Scheduled"/>, after its
	/// 	incoming continuation triggers are evaluated.
	/// </remarks>
	ValueTask<bool> TryTriggerAsync(
		JobHandle jobHandle,
		string expectedJobName,
		DateTimeOffset dueAt,
		CancellationToken cancellationToken = default
	);

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
	/// <remarks>
	/// 	When <see cref="JobAcquisitionRequest.FairQueues"/> is set, implementations must order grouped work by
	/// 	the supplied fair-queue policy.
	/// </remarks>
	ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Records OpenTelemetry correlation for the active execution attempt.
	/// </summary>
	/// <param name="jobHandle">
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
		JobHandle jobHandle,
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
	/// <param name="jobHandle">
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
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Marks an active job successful.
	/// </summary>
	/// <param name="jobHandle">
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
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Reschedules or dead-letters a failed attempt.
	/// </summary>
	/// <param name="jobHandle">
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
		JobHandle jobHandle,
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
	ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Returns a sequence of non-completed jobs for a particular name.
	/// </summary>
	/// <param name="jobName">
	/// 	The name of the job for which to get open jobs.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The jobs matching the query.
	/// </returns>
	/// <remarks>
	///		Used primarily by the scheduling service to materialize recurring jobs,
	///		but offered publicly for general use.
	/// </remarks>
	ValueTask<IReadOnlyList<JobRecord>> QueryNonCompletedJobsAsync(
		string jobName,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Returns retained executions for one job, newest first unless an exact ordinal is requested.
	/// </summary>
	/// <param name="jobHandle">
	///		The job identifier.
	/// </param>
	/// <param name="query">
	/// 	The exact-ordinal filter and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The matching retained executions, or an empty list when the job does not exist.
	/// </returns>
	ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
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
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The job status, or <see langword="null"/> when the invocation does not exist.
	/// </returns>
	ValueTask<JobStatus?> GetJobStatusAsync(JobHandle jobHandle, CancellationToken cancellationToken = default);

	/// <summary>
	///		Moves a non-terminal invocation to the cancelled state.
	/// </summary>
	/// <param name="jobHandle">
	///		The non-terminal invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	///		A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	///		A value task that represents the asynchronous cancellation.
	/// </returns>
	ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Moves a failed invocation back to pending or fast-forwards a scheduled invocation.
	/// </summary>
	/// <param name="jobHandle">
	/// 	The failed or scheduled invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous retry operation.
	/// </returns>
	ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Deletes a terminal invocation.
	/// </summary>
	/// <param name="jobHandle">
	/// 	The terminal invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous deletion.
	/// </returns>
	ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default);

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
	/// <returns>
	/// 	<see langword="true"/> when the provider is reachable; otherwise, <see langword="false"/>.
	/// </returns>
	ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Creates or updates a recurring schedule.
	/// </summary>
	/// <param name="schedule">
	/// 	The recurring schedule to create or update.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous upsert.
	/// </returns>
	ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Removes a dynamic recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The recurring schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous removal.
	/// </returns>
	ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Pauses a recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The recurring schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous pause operation.
	/// </returns>
	ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Resumes a recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The recurring schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous resume operation.
	/// </returns>
	ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Returns schedules ready to materialize.
	/// </summary>
	/// <param name="now">
	/// 	The current UTC time used to determine which schedules are due.
	/// </param>
	/// <param name="batchSize">
	/// 	The maximum number of schedules to return.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The recurring schedules that are ready to materialize.
	/// </returns>
	ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Atomically creates a recurring invocation and advances the schedule.
	/// </summary>
	/// <param name="schedule">
	/// 	The recurring schedule being materialized.
	/// </param>
	/// <param name="job">
	/// 	The invocation to insert.
	/// </param>
	/// <param name="nextRunAt">
	/// 	The next UTC occurrence for the schedule.
	/// </param>
	/// <param name="dependencies">
	///		A list of continuation edges for use with <see cref="OverlapPolicy.Queue"/>.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	///		<see langword="true"/> when the occurrence was materialized; otherwise, <see langword="false"/>.
	/// </returns>
	ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		IReadOnlyList<JobContinuationEdge>? dependencies = null,
		CancellationToken cancellationToken = default
	);
}

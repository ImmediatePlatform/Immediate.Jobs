namespace Immediate.Jobs.Shared;

/// <summary>The storage seam implemented by all job providers.</summary>
public interface IJobStorage
{
	/// <summary>Creates or upgrades provider storage.</summary>
	ValueTask InitializeAsync(CancellationToken cancellationToken = default);

	/// <summary>Inserts a pending or scheduled invocation.</summary>
	ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default);

	/// <summary>Atomically inserts a child invocation and its continuation dependencies.</summary>
	ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	);

	/// <summary>Atomically inserts a batch header, all members, and all dependency edges.</summary>
	ValueTask EnqueueBatchAsync(
		JobBatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	);

	/// <summary>Atomically claims due work in the requested queue and job-capacity order.</summary>
	ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	);

	/// <summary>Extends the lease on an active job owned by the worker.</summary>
	ValueTask RenewLeaseAsync(string jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default);

	/// <summary>Marks an active job successful.</summary>
	ValueTask CompleteAsync(string jobId, string workerId, CancellationToken cancellationToken = default);

	/// <summary>Marks an active job successful and atomically flushes its gated dynamic continuations.</summary>
	ValueTask CompleteWithContinuationsAsync(
		string jobId,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	);

	/// <summary>Immediately adds a concurrent member to the batch of a running job.</summary>
	ValueTask AddBatchJobAsync(
		string currentJobId,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	);

	/// <summary>Reschedules or dead-letters a failed attempt.</summary>
	ValueTask FailAsync(
		string jobId,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	);

	/// <summary>Creates or updates a recurring schedule.</summary>
	ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default);

	/// <summary>Removes code-defined schedules that are not in the supplied active schedule names.</summary>
	ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	);

	/// <summary>Removes a dynamic recurring schedule.</summary>
	ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>Pauses a recurring schedule.</summary>
	ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>Resumes a recurring schedule.</summary>
	ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>Returns schedules ready to materialize.</summary>
	ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	);

	/// <summary>Atomically creates a recurring invocation and advances the schedule.</summary>
	ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	);

	/// <summary>Returns aggregate monitoring data.</summary>
	ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default);

	/// <summary>Returns jobs matching a dashboard query.</summary>
	ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default);

	/// <summary>Gets aggregate progress for one batch.</summary>
	ValueTask<BatchStatus?> GetBatchStatusAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>Queries batch headers for dashboard presentation.</summary>
	ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		JobBatchQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>Queries members of one batch.</summary>
	ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	);

	/// <summary>Gets the durable dependency graph for one batch.</summary>
	ValueTask<BatchGraph?> GetBatchGraphAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>Gets one job and its incoming dependencies.</summary>
	ValueTask<JobStatus?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>Cancels every non-terminal member of an executing batch.</summary>
	ValueTask CancelBatchAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>Deletes a terminal batch, all of its members, and all related edges.</summary>
	ValueTask DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default);

	/// <summary>Moves a failed invocation back to pending.</summary>
	ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>Deletes a terminal invocation.</summary>
	ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default);

	/// <summary>Deletes terminal history older than the supplied retention periods.</summary>
	ValueTask PurgeAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	);

	/// <summary>Records scheduler liveness for monitoring.</summary>
	ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default);

	/// <summary>Checks provider connectivity.</summary>
	ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider capability required by single-server mode to mirror the exact set of jobs selected by
/// its authoritative in-process queue. Custom providers only need this capability when used as a
/// single-server durable replica.
/// </summary>
public interface IJobStorageReplica
{
	/// <summary>Acquires the specified due invocations for the supplied worker.</summary>
	ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<string> jobIds,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	);
}

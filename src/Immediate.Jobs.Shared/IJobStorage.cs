namespace Immediate.Jobs.Shared;

/// <summary>The storage seam implemented by all job providers.</summary>
public interface IJobStorage
{
	/// <summary>Creates or upgrades provider storage.</summary>
	ValueTask InitializeAsync(CancellationToken cancellationToken = default);

	/// <summary>Inserts a pending or scheduled invocation.</summary>
	ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default);

	/// <summary>Atomically claims due work in the requested queue and job-capacity order.</summary>
	ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	);

	/// <summary>Extends the lease on an active job owned by the worker.</summary>
	ValueTask RenewLeaseAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default);

	/// <summary>Marks an active job successful.</summary>
	ValueTask CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default);

	/// <summary>Reschedules or dead-letters a failed attempt.</summary>
	ValueTask FailAsync(
		Guid jobId,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	);

	/// <summary>Creates or updates a recurring schedule.</summary>
	ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default);

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

	/// <summary>Moves a failed invocation back to pending.</summary>
	ValueTask RetryAsync(Guid jobId, CancellationToken cancellationToken = default);

	/// <summary>Deletes a terminal invocation.</summary>
	ValueTask DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);

	/// <summary>Deletes terminal history older than the supplied retention periods.</summary>
	ValueTask PurgeAsync(TimeSpan succeededRetention, TimeSpan failedRetention, CancellationToken cancellationToken = default);

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
		IReadOnlyCollection<Guid> jobIds,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	);
}

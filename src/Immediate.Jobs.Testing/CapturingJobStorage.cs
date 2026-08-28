using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Testing;

/// <summary>
/// An in-memory storage provider that records the data written by job and batch schedulers.
/// </summary>
/// <remarks>
/// Capturing does not alter storage behavior: every operation is forwarded to an
/// <see cref="InMemoryJobStorage"/> after its input has been recorded.
/// </remarks>
public sealed class CapturingJobStorage(TimeProvider timeProvider) :
	IRecurringJobStorage,
	IJobGraphStorage,
	IFairQueueStorage
{
	private readonly Lock _gate = new();
	private readonly InMemoryJobStorage _inner = new(timeProvider);
	private readonly List<JobRecord> _jobs = [];
	private readonly List<ContinuationCapture> _continuations = [];
	private readonly List<BatchCapture> _batches = [];
	private readonly List<BatchJobCapture> _batchJobs = [];
	private readonly List<DynamicContinuationCapture> _dynamicContinuations = [];
	private readonly List<RecurringJobSchedule> _recurringSchedules = [];
	private readonly List<RecurringOperationCapture> _recurringOperations = [];
	private readonly List<RecurringMaterializationCapture> _recurringMaterializations = [];

	/// <summary>Gets snapshots of all job records submitted by schedulers.</summary>
	public IReadOnlyList<JobRecord> Jobs { get { lock (_gate) return [.. _jobs]; } }

	/// <summary>Gets snapshots of continuation enqueue operations.</summary>
	public IReadOnlyList<ContinuationCapture> Continuations { get { lock (_gate) return [.. _continuations]; } }

	/// <summary>Gets snapshots of committed batch enqueue operations.</summary>
	public IReadOnlyList<BatchCapture> Batches { get { lock (_gate) return [.. _batches]; } }

	/// <summary>Gets snapshots of jobs dynamically added to running batches.</summary>
	public IReadOnlyList<BatchJobCapture> BatchJobs { get { lock (_gate) return [.. _batchJobs]; } }

	/// <summary>Gets snapshots of continuations buffered by running jobs.</summary>
	public IReadOnlyList<DynamicContinuationCapture> DynamicContinuations { get { lock (_gate) return [.. _dynamicContinuations]; } }

	/// <summary>Gets recurring schedules submitted for creation or update.</summary>
	public IReadOnlyList<RecurringJobSchedule> RecurringSchedules { get { lock (_gate) return [.. _recurringSchedules]; } }

	/// <summary>Gets recurring schedule mutation calls in call order.</summary>
	public IReadOnlyList<RecurringOperationCapture> RecurringOperations { get { lock (_gate) return [.. _recurringOperations]; } }

	/// <summary>Gets recurring occurrence materialization attempts.</summary>
	public IReadOnlyList<RecurringMaterializationCapture> RecurringMaterializations { get { lock (_gate) return [.. _recurringMaterializations]; } }

	/// <summary>Returns the captured job with the supplied identifier, or <see langword="null"/>.</summary>
	public JobRecord? FindJob(JobHandle jobHandle)
	{
		lock (_gate)
			return _jobs.LastOrDefault(job => job.JobHandle == jobHandle);
	}

	/// <summary>Returns the captured batch with the supplied identifier, or <see langword="null"/>.</summary>
	public BatchCapture? FindBatch(BatchHandle batchHandle)
	{
		lock (_gate)
			return _batches.LastOrDefault(batch => batch.Batch.BatchHandle == batchHandle);
	}

	/// <summary>Clears captured inputs without changing persisted in-memory state.</summary>
	public void Clear()
	{
		lock (_gate)
		{
			_jobs.Clear();
			_continuations.Clear();
			_batches.Clear();
			_batchJobs.Clear();
			_dynamicContinuations.Clear();
			_recurringSchedules.Clear();
			_recurringOperations.Clear();
			_recurringMaterializations.Clear();
		}
	}

	/// <inheritdoc />
	public ValueTask DisposeAsync() => _inner.DisposeAsync();
	/// <inheritdoc />
	public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

	/// <inheritdoc />
	public ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		Capture(job);
		return _inner.EnqueueAsync(job, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask EnqueueContinuationAsync(JobRecord job, IReadOnlyList<JobContinuationEdge> edges, CancellationToken cancellationToken = default)
	{
		var edgeSnapshot = edges.ToArray();
		lock (_gate)
		{
			_jobs.Add(job);
			_continuations.Add(new(job, edgeSnapshot));
		}

		return _inner.EnqueueContinuationAsync(job, edges, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask EnqueueBatchAsync(BatchRecord batch, IReadOnlyList<JobRecord> jobs, IReadOnlyList<JobContinuationEdge> edges, CancellationToken cancellationToken = default)
	{
		var jobSnapshot = jobs.ToArray();
		var edgeSnapshot = edges.ToArray();
		lock (_gate)
		{
			_jobs.AddRange(jobSnapshot);
			_batches.Add(new(batch, jobSnapshot, edgeSnapshot));
		}

		return _inner.EnqueueBatchAsync(batch, jobs, edges, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask AddBatchJobAsync(JobHandle currentJobHandle, int executionNumber, JobRecord job, ContinuationOptions options, CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			_jobs.Add(job);
			_batchJobs.Add(new(currentJobHandle, executionNumber, job, options));
		}

		return _inner.AddBatchJobAsync(currentJobHandle, executionNumber, job, options, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask CompleteWithContinuationsAsync(JobHandle jobHandle, int executionNumber, string workerId, IReadOnlyList<JobContinuationAddition> additions, CancellationToken cancellationToken = default)
	{
		var snapshot = additions.ToArray();
		lock (_gate)
		{
			_jobs.AddRange(snapshot.Select(static addition => addition.Job));
			_dynamicContinuations.Add(new(jobHandle, executionNumber, workerId, snapshot));
		}

		return _inner.CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, additions, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		lock (_gate)
		{
			_recurringSchedules.Add(schedule);
			_recurringOperations.Add(new(RecurringOperation.Upsert, schedule.Name));
		}

		return _inner.UpsertRecurringAsync(schedule, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<bool> MaterializeRecurringAsync(RecurringJobSchedule schedule, JobRecord job, DateTimeOffset nextRunAt, CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			_jobs.Add(job);
			_recurringMaterializations.Add(new(schedule, job, nextRunAt));
		}

		return _inner.MaterializeRecurringAsync(schedule, job, nextRunAt, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(JobAcquisitionRequest request, CancellationToken cancellationToken = default) => _inner.AcquireDueJobsAsync(request, cancellationToken);
	/// <inheritdoc />
	public ValueTask SetExecutionTelemetryAsync(JobHandle jobHandle, int executionNumber, string workerId, string? traceId, string? spanId, DateTimeOffset startedAt, CancellationToken cancellationToken = default) => _inner.SetExecutionTelemetryAsync(jobHandle, executionNumber, workerId, traceId, spanId, startedAt, cancellationToken);
	/// <inheritdoc />
	public ValueTask RenewLeaseAsync(JobHandle jobHandle, int executionNumber, string workerId, TimeSpan lease, CancellationToken cancellationToken = default) => _inner.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);
	/// <inheritdoc />
	public ValueTask CompleteAsync(JobHandle jobHandle, int executionNumber, string workerId, CancellationToken cancellationToken = default) => _inner.CompleteAsync(jobHandle, executionNumber, workerId, cancellationToken);
	/// <inheritdoc />
	public ValueTask FailAsync(JobHandle jobHandle, int executionNumber, string workerId, string error, DateTimeOffset? nextRetryAt, CancellationToken cancellationToken = default) => _inner.FailAsync(jobHandle, executionNumber, workerId, error, nextRetryAt, cancellationToken);
	/// <inheritdoc />
	public ValueTask RemoveObsoleteCodeDefinedRecurringAsync(IReadOnlyCollection<string> activeScheduleNames, CancellationToken cancellationToken = default) => _inner.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken);
	/// <inheritdoc />
	public ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		lock (_gate) _recurringOperations.Add(new(RecurringOperation.Remove, name));
		return _inner.RemoveRecurringAsync(name, cancellationToken);
	}
	/// <inheritdoc />
	public ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		lock (_gate) _recurringOperations.Add(new(RecurringOperation.Pause, name));
		return _inner.PauseRecurringAsync(name, cancellationToken);
	}
	/// <inheritdoc />
	public ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		lock (_gate) _recurringOperations.Add(new(RecurringOperation.Resume, name));
		return _inner.ResumeRecurringAsync(name, cancellationToken);
	}
	/// <inheritdoc />
	public ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default) => _inner.GetDueRecurringAsync(now, batchSize, cancellationToken);
	/// <inheritdoc />
	public ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default) => _inner.GetMonitoringSnapshotAsync(cancellationToken);
	/// <inheritdoc />
	public ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default) => _inner.QueryJobsAsync(query, cancellationToken);
	/// <inheritdoc />
	public ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(JobHandle jobHandle, JobExecutionQuery query, CancellationToken cancellationToken = default) => _inner.QueryJobExecutionsAsync(jobHandle, query, cancellationToken);
	/// <inheritdoc />
	public ValueTask<BatchStatus?> GetBatchStatusAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default) => _inner.GetBatchStatusAsync(batchHandle, cancellationToken);
	/// <inheritdoc />
	public ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(BatchQuery query, CancellationToken cancellationToken = default) => _inner.QueryBatchesAsync(query, cancellationToken);
	/// <inheritdoc />
	public ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(BatchHandle batchHandle, BatchMemberQuery query, CancellationToken cancellationToken = default) => _inner.QueryBatchMembersAsync(batchHandle, query, cancellationToken);
	/// <inheritdoc />
	public ValueTask<BatchGraph?> GetBatchGraphAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default) => _inner.GetBatchGraphAsync(batchHandle, cancellationToken);
	/// <inheritdoc />
	public ValueTask<JobStatus?> GetJobStatusAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) => _inner.GetJobStatusAsync(jobHandle, cancellationToken);
	/// <inheritdoc />
	public ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default) => _inner.CancelBatchAsync(batchHandle, cancellationToken);
	/// <inheritdoc />
	public ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default) => _inner.DeleteBatchAsync(batchHandle, cancellationToken);
	/// <inheritdoc />
	public ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) => _inner.CancelAsync(jobHandle, cancellationToken);
	/// <inheritdoc />
	public ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) => _inner.RetryAsync(jobHandle, cancellationToken);
	/// <inheritdoc />
	public ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) => _inner.DeleteAsync(jobHandle, cancellationToken);
	/// <inheritdoc />
	public ValueTask PurgeJobsAsync(TimeSpan succeededRetention, TimeSpan failedRetention, CancellationToken cancellationToken = default) => _inner.PurgeJobsAsync(succeededRetention, failedRetention, cancellationToken);
	/// <inheritdoc />
	public ValueTask PurgeBatchesAsync(TimeSpan batchSucceededRetention, TimeSpan batchFailedRetention, CancellationToken cancellationToken = default) => _inner.PurgeBatchesAsync(batchSucceededRetention, batchFailedRetention, cancellationToken);
	/// <inheritdoc />
	public ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default) => _inner.HeartbeatAsync(server, cancellationToken);
	/// <inheritdoc />
	public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => _inner.IsHealthyAsync(cancellationToken);

	private void Capture(JobRecord job)
	{
		lock (_gate) _jobs.Add(job);
	}
}

/// <summary>A captured continuation enqueue operation.</summary>
public sealed record ContinuationCapture(JobRecord Job, IReadOnlyList<JobContinuationEdge> Edges);

/// <summary>A captured atomic batch enqueue operation.</summary>
public sealed record BatchCapture(BatchRecord Batch, IReadOnlyList<JobRecord> Jobs, IReadOnlyList<JobContinuationEdge> Edges);

/// <summary>A captured dynamic batch-member operation.</summary>
public sealed record BatchJobCapture(JobHandle CurrentJobHandle, int ExecutionNumber, JobRecord Job, ContinuationOptions Options);

/// <summary>A captured set of continuations flushed when a running job completed.</summary>
public sealed record DynamicContinuationCapture(JobHandle JobHandle, int ExecutionNumber, string WorkerId, IReadOnlyList<JobContinuationAddition> Additions);

/// <summary>The kind of recurring schedule mutation that was captured.</summary>
public enum RecurringOperation
{
	/// <summary>A schedule was created or updated.</summary>
	Upsert,
	/// <summary>A schedule was removed.</summary>
	Remove,
	/// <summary>A schedule was paused.</summary>
	Pause,
	/// <summary>A schedule was resumed.</summary>
	Resume,
}

/// <summary>A captured recurring schedule mutation.</summary>
public sealed record RecurringOperationCapture(RecurringOperation Operation, string Name);

/// <summary>A captured recurring occurrence materialization attempt.</summary>
public sealed record RecurringMaterializationCapture(RecurringJobSchedule Schedule, JobRecord Job, DateTimeOffset NextRunAt);

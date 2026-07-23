namespace Immediate.Jobs.Shared;

/// <summary>
/// A single-server storage topology that executes against an authoritative in-process store while
/// synchronously replicating changes to durable storage and restoring them when the process starts.
/// </summary>
public sealed class SingleServerJobStorage : IJobStorage, IAsyncDisposable, IDisposable
{
	private const int RecoveryBatchSize = 1000;
	private readonly TimeProvider _timeProvider;
	private readonly SemaphoreSlim _initialization = new(1, 1);
	private InMemoryJobStorage _primary;
	private bool _initialized;
	private bool _disposed;

	/// <summary>Creates a memory-primary store backed by the supplied durable replica.</summary>
	public SingleServerJobStorage(IJobStorage durableStorage, TimeProvider timeProvider)
	{
		ArgumentNullException.ThrowIfNull(durableStorage);
		ArgumentNullException.ThrowIfNull(timeProvider);
		if (durableStorage is SingleServerJobStorage)
			throw new ArgumentException("A single-server store cannot be used as its own durable replica.", nameof(durableStorage));
		if (durableStorage is not IJobStorageReplica)
			throw new ArgumentException("Single-server durable storage must implement IJobStorageReplica.", nameof(durableStorage));

		DurableStorage = durableStorage;
		_timeProvider = timeProvider;
		_primary = new(timeProvider);
	}

	/// <summary>The in-process authoritative store.</summary>
	public IJobStorage PrimaryStorage => _primary;

	/// <summary>The durable write-through replica.</summary>
	public IJobStorage DurableStorage { get; }

	/// <inheritdoc />
	public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => EnsureInitializedAsync(cancellationToken);

	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
		await _primary.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.EnqueueContinuationAsync(job, edges, cancellationToken).ConfigureAwait(false);
		await _primary.EnqueueContinuationAsync(job, edges, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		JobBatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.EnqueueBatchAsync(batch, jobs, edges, cancellationToken).ConfigureAwait(false);
		await _primary.EnqueueBatchAsync(batch, jobs, edges, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		var acquired = await _primary.AcquireDueJobsAsync(request, cancellationToken).ConfigureAwait(false);
		if (acquired.Count == 0)
			return acquired;

		var replica = (IJobStorageReplica)DurableStorage;
		var replicated = await replica.AcquireJobsAsync(
			[.. acquired.Select(x => x.Id)],
			request.WorkerId,
			request.Lease,
			cancellationToken
		).ConfigureAwait(false);
		if (acquired.Count != replicated.Count ||
			!acquired.Select(x => x.Id).ToHashSet(StringComparer.Ordinal).SetEquals(replicated.Select(x => x.Id)))
		{
			throw new ImmediateJobException(
				"The durable job replica has drifted from the authoritative in-memory queue. " +
				"Single-server mode must not be used by multiple scheduler processes."
			);
		}

		return acquired;
	}

	/// <inheritdoc />
	public async ValueTask SetExecutionTelemetryAsync(
		string jobId,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.SetExecutionTelemetryAsync(
			jobId,
			workerId,
			traceId,
			spanId,
			startedAt,
			cancellationToken
		).ConfigureAwait(false);
		await _primary.SetExecutionTelemetryAsync(
			jobId,
			workerId,
			traceId,
			spanId,
			startedAt,
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		string jobId,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RenewLeaseAsync(jobId, workerId, lease, cancellationToken).ConfigureAwait(false);
		await _primary.RenewLeaseAsync(jobId, workerId, lease, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask CompleteAsync(string jobId, string workerId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.CompleteAsync(jobId, workerId, cancellationToken).ConfigureAwait(false);
		await _primary.CompleteAsync(jobId, workerId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask CompleteWithContinuationsAsync(
		string jobId,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.CompleteWithContinuationsAsync(jobId, workerId, additions, cancellationToken)
			.ConfigureAwait(false);
		await _primary.CompleteWithContinuationsAsync(jobId, workerId, additions, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask AddBatchJobAsync(
		string currentJobId,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.AddBatchJobAsync(currentJobId, job, options, cancellationToken).ConfigureAwait(false);
		await _primary.AddBatchJobAsync(currentJobId, job, options, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask FailAsync(
		string jobId,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.FailAsync(jobId, workerId, error, nextRetryAt, cancellationToken).ConfigureAwait(false);
		await _primary.FailAsync(jobId, workerId, error, nextRetryAt, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.UpsertRecurringAsync(schedule, cancellationToken).ConfigureAwait(false);
		await _primary.UpsertRecurringAsync(schedule, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken)
			.ConfigureAwait(false);
		await _primary.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RemoveRecurringAsync(name, cancellationToken).ConfigureAwait(false);
		await _primary.RemoveRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.PauseRecurringAsync(name, cancellationToken).ConfigureAwait(false);
		await _primary.PauseRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.ResumeRecurringAsync(name, cancellationToken).ConfigureAwait(false);
		await _primary.ResumeRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetDueRecurringAsync(now, batchSize, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		if (!await DurableStorage.MaterializeRecurringAsync(schedule, job, nextRunAt, cancellationToken).ConfigureAwait(false))
			return false;
		return await _primary.MaterializeRecurringAsync(schedule, job, nextRunAt, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.QueryJobsAsync(query, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchStatusAsync(
		string batchId,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetBatchStatusAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		JobBatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.QueryBatchesAsync(query, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.QueryBatchMembersAsync(batchId, query, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		string batchId,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetBatchGraphAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		string jobId,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetJobStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.CancelBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
		await _primary.CancelBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.DeleteBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
		await _primary.DeleteBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RetryAsync(jobId, cancellationToken).ConfigureAwait(false);
		await _primary.RetryAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
		await _primary.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask PurgeAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.PurgeAsync(
			succeededRetention,
			failedRetention,
			batchSucceededRetention,
			batchFailedRetention,
			cancellationToken
		).ConfigureAwait(false);
		await _primary.PurgeAsync(
			succeededRetention,
			failedRetention,
			batchSucceededRetention,
			batchFailedRetention,
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _primary.HeartbeatAsync(server, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await DurableStorage.IsHealthyAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		if (DurableStorage is IDisposable disposable)
			disposable.Dispose();
		else if (DurableStorage is IAsyncDisposable asyncDisposable)
			asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_initialization.Dispose();
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;
		_disposed = true;
		if (DurableStorage is IAsyncDisposable asyncDisposable)
			await asyncDisposable.DisposeAsync().ConfigureAwait(false);
		else if (DurableStorage is IDisposable disposable)
			disposable.Dispose();
		_initialization.Dispose();
	}

	private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (Volatile.Read(ref _initialized))
			return;

		await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (Volatile.Read(ref _initialized))
				return;

			await DurableStorage.InitializeAsync(cancellationToken).ConfigureAwait(false);
			var recoveredPrimary = new InMemoryJobStorage(_timeProvider);
			await recoveredPrimary.InitializeAsync(cancellationToken).ConfigureAwait(false);

			var recoveredJobs = new List<JobRecord>();
			foreach (var state in Enum.GetValues<JobState>())
			{
				var skip = 0;
				while (true)
				{
					var jobs = await DurableStorage.QueryJobsAsync(
						new() { State = state, Skip = skip, Take = RecoveryBatchSize },
						cancellationToken
					).ConfigureAwait(false);
					recoveredJobs.AddRange(jobs);
					if (jobs.Count < RecoveryBatchSize)
						break;
					skip += jobs.Count;
				}
			}

			var batchIds = recoveredJobs
				.Where(static job => job.BatchId is not null)
				.Select(static job => job.BatchId!)
				.Distinct(StringComparer.Ordinal)
				.ToArray();
			var recoveredBatches = new Dictionary<string, RecoveredBatch>(batchIds.Length, StringComparer.Ordinal);
			foreach (var batchId in batchIds)
			{
				var status = await DurableStorage.GetBatchStatusAsync(batchId, cancellationToken).ConfigureAwait(false)
					?? throw new ImmediateJobException($"Batch '{batchId}' has members but no durable batch header.");
				var graph = await DurableStorage.GetBatchGraphAsync(batchId, cancellationToken).ConfigureAwait(false)
					?? throw new ImmediateJobException($"Batch '{batchId}' has members but no durable dependency graph.");
				recoveredBatches.Add(batchId, new(
					new()
					{
						Id = status.Id,
						CreatedAt = status.CreatedAt,
						TotalJobs = status.Total,
						PendingCount = status.Remaining,
						SucceededCount = status.Succeeded,
						FailedCount = status.Failed,
						CancelledCount = status.Cancelled,
						StartedAt = status.StartedAt,
						CompletedAt = status.CompletedAt,
						State = status.State,
					},
					[.. recoveredJobs.Where(job => job.BatchId == batchId)],
					[.. graph.Edges.Select(ToContinuationEdge)]
				));
			}

			var restoredBatchIds = new HashSet<string>(StringComparer.Ordinal);
			while (recoveredBatches.Count != 0)
			{
				var ready = recoveredBatches.Values
					.Where(batch => batch.Edges
						.Where(static edge => edge.ParentBatchId is not null)
						.All(edge => restoredBatchIds.Contains(edge.ParentBatchId!)))
					.OrderBy(static batch => batch.Record.CreatedAt)
					.ThenBy(static batch => batch.Record.Id, StringComparer.Ordinal)
					.ToArray();
				if (ready.Length == 0)
				{
					var unresolved = string.Join(", ", recoveredBatches.Keys.Order(StringComparer.Ordinal));
					throw new ImmediateJobException(
						$"Durable batches have cyclic or missing parent-batch dependencies: {unresolved}."
					);
				}

				foreach (var batch in ready)
				{
					await recoveredPrimary.EnqueueBatchAsync(
						batch.Record,
						batch.Jobs,
						batch.Edges,
						cancellationToken
					).ConfigureAwait(false);
					_ = recoveredBatches.Remove(batch.Record.Id);
					_ = restoredBatchIds.Add(batch.Record.Id);
				}
			}

			foreach (var job in recoveredJobs.Where(static job => job.BatchId is null))
			{
				var status = await DurableStorage.GetJobStatusAsync(job.Id, cancellationToken).ConfigureAwait(false)
					?? throw new ImmediateJobException($"Job '{job.Id}' was queried but has no durable status.");
				if (status.DependsOn.Count == 0)
					await recoveredPrimary.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
				else
					await recoveredPrimary.EnqueueContinuationAsync(
						job,
						[.. status.DependsOn.Select(ToContinuationEdge)],
						cancellationToken
					).ConfigureAwait(false);
			}

			var snapshot = await DurableStorage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
			foreach (var schedule in snapshot.Recurring)
				await recoveredPrimary.UpsertRecurringAsync(schedule, cancellationToken).ConfigureAwait(false);

			_primary = recoveredPrimary;
			Volatile.Write(ref _initialized, true);
		}
		finally
		{
			_ = _initialization.Release();
		}
	}

	private static JobContinuationEdge ToContinuationEdge(BatchGraphEdge edge) => new()
	{
		ChildJobId = edge.ChildJobId,
		ParentJobId = edge.ParentJobId,
		ParentBatchId = edge.ParentBatchId,
		Trigger = edge.Trigger,
	};

	private sealed record RecoveredBatch(
		JobBatchRecord Record,
		IReadOnlyList<JobRecord> Jobs,
		IReadOnlyList<JobContinuationEdge> Edges
	);
}

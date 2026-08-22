using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// A single-server storage topology that executes against an authoritative in-process store while
/// synchronously replicating changes to durable storage and restoring them when the process starts.
/// </summary>
internal sealed class SingleServerJobStorage :
	IRecurringJobStorage,
	IJobGraphStorage,
	IFairQueueStorage,
	IAsyncDisposable,
	IDisposable
{
	private const int RecoveryBatchSize = 1000;
	private readonly TimeProvider _timeProvider;
#pragma warning disable CA2213 // Kept alive so callers already waiting during disposal can safely leave the semaphores.
	private readonly SemaphoreSlim _initialization = new(1, 1);
	private readonly SemaphoreSlim _recurringMaterialization = new(1, 1);
#pragma warning restore CA2213
	private readonly IRecurringJobStorage _recurringDurableStorage;
	private readonly IJobGraphStorage _graphDurableStorage;
	private readonly IJobGraphStorageReplica _graphDurableReplica;
	private readonly Lock _disposeGate = new();
	private InMemoryJobStorage _primary;
	private bool _initialized;
	private Task? _disposeTask;
	private int _disposeStarted;

	/// <summary>
	/// 	Creates a memory-primary store backed by the supplied durable replica.
	/// </summary>
	/// <param name="durableStorage">
	/// 	The durable write-through replica.
	/// </param>
	/// <param name="timeProvider">
	/// 	The clock used by the in-process primary store.
	/// </param>
	/// <remarks>
	/// 	The wrapper takes ownership of <paramref name="durableStorage"/> and disposes it with the primary store.
	/// </remarks>
	public SingleServerJobStorage(IJobStorage durableStorage, TimeProvider timeProvider)
	{
		ArgumentNullException.ThrowIfNull(durableStorage);
		ArgumentNullException.ThrowIfNull(timeProvider);
		if (durableStorage is SingleServerJobStorage)
			throw new ArgumentException("A single-server store cannot be used as its own durable replica.", nameof(durableStorage));
		if (durableStorage is not IJobStorageReplica)
			throw new ArgumentException("Single-server durable storage must implement IJobStorageReplica.", nameof(durableStorage));
		if (durableStorage is not IRecurringJobStorage recurringDurableStorage)
			throw new ArgumentException("Single-server durable storage must support recurring jobs.", nameof(durableStorage));
		if (durableStorage is not IJobGraphStorage graphDurableStorage)
			throw new ArgumentException("Single-server durable storage must support batches and continuations.", nameof(durableStorage));
		if (durableStorage is not IJobGraphStorageReplica graphDurableReplica)
			throw new ArgumentException("Single-server durable storage must implement IJobGraphStorageReplica.", nameof(durableStorage));

		DurableStorage = durableStorage;
		_recurringDurableStorage = recurringDurableStorage;
		_graphDurableStorage = graphDurableStorage;
		_graphDurableReplica = graphDurableReplica;
		_timeProvider = timeProvider;
		_primary = new(timeProvider);
	}

	/// <summary>
	/// 	The in-process authoritative store.
	/// </summary>
	/// <value>
	/// 	The in-process primary storage provider.
	/// </value>
	public IJobStorage PrimaryStorage => _primary;

	/// <summary>
	/// 	The durable write-through replica.
	/// </summary>
	/// <value>
	/// 	The durable storage provider replicated by this wrapper.
	/// </value>
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
		await _graphDurableStorage.EnqueueContinuationAsync(job, edges, cancellationToken).ConfigureAwait(false);
		await _primary.EnqueueContinuationAsync(job, edges, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _graphDurableStorage.EnqueueBatchAsync(batch, jobs, edges, cancellationToken).ConfigureAwait(false);
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
			[.. acquired.Select(x => x.JobId)],
			request.WorkerId,
			request.Lease,
			cancellationToken
		).ConfigureAwait(false);

		var replicatedExecutions = replicated.ToDictionary(static job => job.JobId, static job => job.Attempt);
		if (acquired.Count != replicated.Count ||
			acquired.Any(job => !replicatedExecutions.TryGetValue(job.JobId, out var attempt) || attempt != job.Attempt))
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
		JobHandle jobId,
		int executionNumber,
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
			executionNumber,
			workerId,
			traceId,
			spanId,
			startedAt,
			cancellationToken
		).ConfigureAwait(false);
		await _primary.SetExecutionTelemetryAsync(
			jobId,
			executionNumber,
			workerId,
			traceId,
			spanId,
			startedAt,
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RenewLeaseAsync(jobId, executionNumber, workerId, lease, cancellationToken).ConfigureAwait(false);
		await _primary.RenewLeaseAsync(jobId, executionNumber, workerId, lease, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.CompleteAsync(jobId, executionNumber, workerId, cancellationToken).ConfigureAwait(false);
		await _primary.CompleteAsync(jobId, executionNumber, workerId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask CompleteWithContinuationsAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _graphDurableStorage.CompleteWithContinuationsAsync(jobId, executionNumber, workerId, additions, cancellationToken)
			.ConfigureAwait(false);
		await _primary.CompleteWithContinuationsAsync(jobId, executionNumber, workerId, additions, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask AddBatchJobAsync(
		JobHandle currentJobId,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _graphDurableStorage.AddBatchJobAsync(currentJobId, executionNumber, job, options, cancellationToken).ConfigureAwait(false);
		await _primary.AddBatchJobAsync(currentJobId, executionNumber, job, options, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask FailAsync(
		JobHandle jobId,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.FailAsync(jobId, executionNumber, workerId, error, nextRetryAt, cancellationToken).ConfigureAwait(false);
		await _primary.FailAsync(jobId, executionNumber, workerId, error, nextRetryAt, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _recurringDurableStorage.UpsertRecurringAsync(schedule, cancellationToken).ConfigureAwait(false);
		await _primary.UpsertRecurringAsync(schedule, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _recurringDurableStorage.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken)
			.ConfigureAwait(false);
		await _primary.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _recurringDurableStorage.RemoveRecurringAsync(name, cancellationToken).ConfigureAwait(false);
		await _primary.RemoveRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _recurringDurableStorage.PauseRecurringAsync(name, cancellationToken).ConfigureAwait(false);
		await _primary.PauseRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _recurringDurableStorage.ResumeRecurringAsync(name, cancellationToken).ConfigureAwait(false);
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
		await _recurringMaterialization.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();
			var durableResult = await _recurringDurableStorage.MaterializeRecurringAsync(
				schedule,
				job,
				nextRunAt,
				cancellationToken
			).ConfigureAwait(false);
			var primaryResult = await _primary.MaterializeRecurringAsync(schedule, job, nextRunAt, cancellationToken)
				.ConfigureAwait(false);
			if (primaryResult != durableResult)
			{
				throw new ImmediateJobException(
					"The durable recurring-job replica has drifted from the authoritative in-memory schedule."
				);
			}

			return primaryResult;
		}
		finally
		{
			_ = _recurringMaterialization.Release();
		}
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
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobId,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		// Recovery restores current jobs and related state into the primary, but not retained executions.
		return await DurableStorage.QueryJobExecutionsAsync(jobId, query, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchStatusAsync(
		BatchHandle batchId,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetBatchStatusAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.QueryBatchesAsync(query, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		BatchHandle batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.QueryBatchMembersAsync(batchId, query, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		BatchHandle batchId,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetBatchGraphAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobId,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		return await _primary.GetJobStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(BatchHandle batchId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _graphDurableStorage.CancelBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
		await _primary.CancelBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(BatchHandle batchId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _graphDurableStorage.DeleteBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
		await _primary.DeleteBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.CancelAsync(jobId, cancellationToken).ConfigureAwait(false);
		await _primary.CancelAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.RetryAsync(jobId, cancellationToken).ConfigureAwait(false);
		await _primary.RetryAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
		await _primary.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await DurableStorage.PurgeJobsAsync(
			succeededRetention,
			failedRetention,
			cancellationToken
		).ConfigureAwait(false);
		await _primary.PurgeJobsAsync(
			succeededRetention,
			failedRetention,
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await _graphDurableStorage.PurgeBatchesAsync(
			batchSucceededRetention,
			batchFailedRetention,
			cancellationToken
		).ConfigureAwait(false);
		await _primary.PurgeBatchesAsync(
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
	public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

	/// <inheritdoc />
	public ValueTask DisposeAsync()
	{
		lock (_disposeGate)
			return new(_disposeTask ??= DisposeCoreAsync());
	}

	private async Task DisposeCoreAsync()
	{
		Volatile.Write(ref _disposeStarted, 1);
		await _initialization.WaitAsync(CancellationToken.None).ConfigureAwait(false);
		try
		{
			await _recurringMaterialization.WaitAsync(CancellationToken.None).ConfigureAwait(false);
			try
			{
				await _primary.DisposeAsync().ConfigureAwait(false);
				await DurableStorage.DisposeAsync().ConfigureAwait(false);
			}
			finally
			{
				_ = _recurringMaterialization.Release();
			}
		}
		finally
		{
			_ = _initialization.Release();
		}
	}

	private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		if (Volatile.Read(ref _initialized))
			return;

		await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
		InMemoryJobStorage? recoveredPrimary = null;
		try
		{
			ThrowIfDisposed();
			if (Volatile.Read(ref _initialized))
				return;

			await DurableStorage.InitializeAsync(cancellationToken).ConfigureAwait(false);
			recoveredPrimary = CreatePrimaryStorage();
			await recoveredPrimary.InitializeAsync(cancellationToken).ConfigureAwait(false);

			var recoveredJobs = new List<JobRecord>();
			var recoveredIncomingEdges = new Dictionary<JobHandle, List<JobContinuationEdge>>();

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

					var standaloneJobs = jobs
						.Where(static job => job.BatchId is null)
						.Select(static job => job.JobId)
						.ToArray();

					if (standaloneJobs.Length != 0)
					{
						// Standalone continuation edges are not represented by a batch graph, so recovery must
						// load them explicitly before it can restore dependency-gated jobs into the primary queue.
						var incomingEdges = await _graphDurableReplica.GetIncomingEdgesAsync(
							standaloneJobs,
							cancellationToken
						).ConfigureAwait(false);

						foreach (var edge in incomingEdges)
						{
							if (!standaloneJobs.Contains(edge.ChildJobId))
							{
								throw new ImmediateJobException(
									$"Durable storage returned an incoming edge for unrequested job '{edge.ChildJobId}'."
								);
							}

							if (!recoveredIncomingEdges.TryGetValue(edge.ChildJobId, out var edges))
								recoveredIncomingEdges.Add(edge.ChildJobId, edges = []);
							edges.Add(edge);
						}
					}

					if (jobs.Count < RecoveryBatchSize)
						break;
					skip += jobs.Count;
				}
			}

			var batchIds = recoveredJobs
				.Where(static job => job.BatchId is not null)
				.Select(static job => job.BatchId!)
				.Distinct()
				.ToArray();

			var recoveredBatches = new Dictionary<BatchHandle, RecoveredBatch>(batchIds.Length);

			foreach (var batchId in batchIds)
			{
				var status = await _graphDurableStorage.GetBatchStatusAsync(batchId, cancellationToken).ConfigureAwait(false)
					?? throw new ImmediateJobException($"Batch '{batchId}' has members but no durable batch header.");
				var graph = await _graphDurableStorage.GetBatchGraphAsync(batchId, cancellationToken).ConfigureAwait(false)
					?? throw new ImmediateJobException($"Batch '{batchId}' has members but no durable dependency graph.");

				recoveredBatches.Add(batchId, new RecoveredBatch
				{
					Record = new()
					{
						BatchId = status.BatchId,
						CreatedAt = status.CreatedAt,
						TotalJobs = status.Total,
						PendingCount = status.Remaining,
						SucceededCount = status.Succeeded,
						FailedCount = status.Failed,
						CancelledCount = status.Cancelled,
						SkippedCount = status.Skipped,
						StartedAt = status.StartedAt,
						CompletedAt = status.CompletedAt,
						State = status.State,
					},
					Jobs = [.. recoveredJobs.Where(job => job.BatchId == batchId)],
					Edges = [.. graph.Edges.Select(ToContinuationEdge)],
				});
			}

			var restoredBatchIds = new HashSet<BatchHandle>();
			while (recoveredBatches.Count != 0)
			{
				var ready = recoveredBatches.Values
					.Where(batch => batch.Edges
						.Where(static edge => edge.ParentBatchId is not null)
						.All(edge => restoredBatchIds.Contains(edge.ParentBatchId!)))
					.OrderBy(static batch => batch.Record.CreatedAt)
					.ThenBy(static batch => batch.Record.BatchId)
					.ToArray();

				if (ready.Length == 0)
				{
					var unresolved = string.Join(", ", recoveredBatches.Keys.Order());
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
					_ = recoveredBatches.Remove(batch.Record.BatchId);
					_ = restoredBatchIds.Add(batch.Record.BatchId);
				}
			}

			var restoredJobIds = recoveredJobs
				.Where(static job => job.BatchId is not null)
				.Select(static job => job.JobId)
				.ToHashSet();

			var allRecoveredJobIds = recoveredJobs.Select(static job => job.JobId).ToHashSet();
			var standaloneContinuations = new Dictionary<JobHandle, JobRecord>();

			foreach (var job in recoveredJobs.Where(static job => job.BatchId is null))
			{
				if (!recoveredIncomingEdges.TryGetValue(job.JobId, out var incomingEdges))
				{
					if (job.State == JobState.AwaitingContinuation || job.RemainingDependencies != 0)
					{
						throw new ImmediateJobException(
							$"Job '{job.JobId}' has continuation dependencies but no durable dependency graph."
						);
					}

					await recoveredPrimary.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
					_ = restoredJobIds.Add(job.JobId);
				}
				else
				{
					standaloneContinuations.Add(job.JobId, job);
				}
			}

			bool AreContinuationParentsRestored(JobRecord job) => recoveredIncomingEdges[job.JobId].All(edge =>
				(edge.ParentJobId is null || restoredJobIds.Contains(edge.ParentJobId))
				&& (edge.ParentBatchId is null || restoredBatchIds.Contains(edge.ParentBatchId))
			);

			while (standaloneContinuations.Count != 0)
			{
				var ready = standaloneContinuations.Values
					.Where(AreContinuationParentsRestored)
					.OrderBy(static job => job.CreatedAt)
					.ThenBy(static job => job.JobId)
					.ToArray();

				if (ready.Length == 0)
				{
					var missingParents = standaloneContinuations.Values
						.SelectMany(job => recoveredIncomingEdges[job.JobId])
						.Where(edge => edge.ParentJobId is { } parentId && !allRecoveredJobIds.Contains(parentId))
						.Select(static edge => edge.ParentJobId!)
						.Distinct()
						.Order()
						.ToList();

					var missingParentBatches = standaloneContinuations.Values
						.SelectMany(job => recoveredIncomingEdges[job.JobId])
						.Where(edge => edge.ParentBatchId is { } parentId && !restoredBatchIds.Contains(parentId))
						.Select(static edge => edge.ParentBatchId!)
						.Distinct()
						.Order()
						.ToList();

					var unresolved = string.Join(", ", standaloneContinuations.Keys.Order());
					if (missingParents.Count != 0)
					{
						throw new ImmediateJobException(
							$"Durable standalone continuations reference missing parent jobs: {string.Join(", ", missingParents)}."
						);
					}

					if (missingParentBatches.Count != 0)
					{
						throw new ImmediateJobException(
							$"Durable standalone continuations reference missing parent batches: {string.Join(", ", missingParentBatches)}."
						);
					}

					throw new ImmediateJobException($"Durable standalone continuations contain a cycle: {unresolved}.");
				}

				foreach (var job in ready)
				{
					await recoveredPrimary.EnqueueContinuationAsync(
						job,
						recoveredIncomingEdges[job.JobId],
						cancellationToken
					).ConfigureAwait(false);
					_ = standaloneContinuations.Remove(job.JobId);
					_ = restoredJobIds.Add(job.JobId);
				}
			}

			var snapshot = await DurableStorage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
			foreach (var schedule in snapshot.Recurring)
				await recoveredPrimary.UpsertRecurringAsync(schedule, cancellationToken).ConfigureAwait(false);

			var previousPrimary = _primary;
			_primary = recoveredPrimary;
			recoveredPrimary = null;
			await previousPrimary.DisposeAsync().ConfigureAwait(false);
			Volatile.Write(ref _initialized, true);
		}
		finally
		{
			if (recoveredPrimary is not null)
				await recoveredPrimary.DisposeAsync().ConfigureAwait(false);
			_ = _initialization.Release();
		}
	}

	private void ThrowIfDisposed() =>
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

	private static JobContinuationEdge ToContinuationEdge(BatchGraphEdge edge) => new()
	{
		ChildJobId = edge.ChildJobId,
		ParentJobId = edge.ParentJobId,
		ParentBatchId = edge.ParentBatchId,
		Trigger = edge.Trigger,
		Delay = TimeSpan.Zero,
	};

	private InMemoryJobStorage CreatePrimaryStorage() => new(_timeProvider);

	private sealed record RecoveredBatch
	{
		public required BatchRecord Record { get; init; }
		public required IReadOnlyList<JobRecord> Jobs { get; init; }
		public required IReadOnlyList<JobContinuationEdge> Edges { get; init; }
	}
}

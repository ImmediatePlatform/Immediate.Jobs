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
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.EnqueueAsync(job, cancellationToken);
		await _primary.EnqueueAsync(job, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _graphDurableStorage.EnqueueContinuationAsync(job, edges, cancellationToken);
		await _primary.EnqueueContinuationAsync(job, edges, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _graphDurableStorage.EnqueueBatchAsync(batch, jobs, edges, cancellationToken);
		await _primary.EnqueueBatchAsync(batch, jobs, edges, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		var acquired = await _primary.AcquireDueJobsAsync(request, cancellationToken);
		if (acquired.Count == 0)
			return acquired;

		var replica = (IJobStorageReplica)DurableStorage;
		var replicated = await replica.AcquireJobsAsync(
			[.. acquired.Select(x => x.JobHandle)],
			request.WorkerId,
			request.Lease,
			cancellationToken
		);

		var replicatedExecutions = replicated.ToDictionary(static job => job.JobHandle, static job => job.Attempt);
		if (acquired.Count != replicated.Count ||
			acquired.Any(job => !replicatedExecutions.TryGetValue(job.JobHandle, out var attempt) || attempt != job.Attempt))
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
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.SetExecutionTelemetryAsync(
			jobHandle,
			executionNumber,
			workerId,
			traceId,
			spanId,
			startedAt,
			cancellationToken
		);
		await _primary.SetExecutionTelemetryAsync(
			jobHandle,
			executionNumber,
			workerId,
			traceId,
			spanId,
			startedAt,
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);
		await _primary.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.CompleteAsync(jobHandle, executionNumber, workerId, cancellationToken);
		await _primary.CompleteAsync(jobHandle, executionNumber, workerId, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask CompleteWithContinuationsAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _graphDurableStorage.CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, additions, cancellationToken);
		await _primary.CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, additions, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask AddBatchJobAsync(
		JobHandle currentJobHandle,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _graphDurableStorage.AddBatchJobAsync(currentJobHandle, executionNumber, job, options, cancellationToken);
		await _primary.AddBatchJobAsync(currentJobHandle, executionNumber, job, options, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask FailAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.FailAsync(jobHandle, executionNumber, workerId, error, nextRetryAt, cancellationToken);
		await _primary.FailAsync(jobHandle, executionNumber, workerId, error, nextRetryAt, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _recurringDurableStorage.UpsertRecurringAsync(schedule, cancellationToken);
		await _primary.UpsertRecurringAsync(schedule, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _recurringDurableStorage.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken);
		await _primary.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _recurringDurableStorage.RemoveRecurringAsync(name, cancellationToken);
		await _primary.RemoveRecurringAsync(name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _recurringDurableStorage.PauseRecurringAsync(name, cancellationToken);
		await _primary.PauseRecurringAsync(name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _recurringDurableStorage.ResumeRecurringAsync(name, cancellationToken);
		await _primary.ResumeRecurringAsync(name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		return await _primary.GetDueRecurringAsync(now, batchSize, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _recurringMaterialization.WaitAsync(cancellationToken);
		try
		{
			ThrowIfDisposed();
			var durableResult = await _recurringDurableStorage.MaterializeRecurringAsync(
				schedule,
				job,
				nextRunAt,
				cancellationToken
			);
			var primaryResult = await _primary.MaterializeRecurringAsync(schedule, job, nextRunAt, cancellationToken);
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
		await EnsureInitializedAsync(cancellationToken);
		return await _primary.GetMonitoringSnapshotAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		return await _primary.QueryJobsAsync(query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobHandle,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		// Recovery restores current jobs and related state into the primary, but not retained executions.
		return await DurableStorage.QueryJobExecutionsAsync(jobHandle, query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchStatusAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		return await _primary.GetBatchStatusAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		return await _primary.QueryBatchesAsync(query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		BatchHandle batchHandle,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		return await _primary.QueryBatchMembersAsync(batchHandle, query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		return await _primary.GetBatchGraphAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobHandle,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		return await _primary.GetJobStatusAsync(jobHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _graphDurableStorage.CancelBatchAsync(batchHandle, cancellationToken);
		await _primary.CancelBatchAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _graphDurableStorage.DeleteBatchAsync(batchHandle, cancellationToken);
		await _primary.DeleteBatchAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.CancelAsync(jobHandle, cancellationToken);
		await _primary.CancelAsync(jobHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.RetryAsync(jobHandle, cancellationToken);
		await _primary.RetryAsync(jobHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.DeleteAsync(jobHandle, cancellationToken);
		await _primary.DeleteAsync(jobHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.PurgeJobsAsync(
			succeededRetention,
			failedRetention,
			cancellationToken
		);
		await _primary.PurgeJobsAsync(
			succeededRetention,
			failedRetention,
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _graphDurableStorage.PurgeBatchesAsync(
			batchSucceededRetention,
			batchFailedRetention,
			cancellationToken
		);
		await _primary.PurgeBatchesAsync(
			batchSucceededRetention,
			batchFailedRetention,
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		await _primary.HeartbeatAsync(server, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);
		return await DurableStorage.IsHealthyAsync(cancellationToken);
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
		await _initialization.WaitAsync(CancellationToken.None);
		try
		{
			await _recurringMaterialization.WaitAsync(CancellationToken.None);
			try
			{
				await _primary.DisposeAsync();
				await DurableStorage.DisposeAsync();
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

		await _initialization.WaitAsync(cancellationToken);
		InMemoryJobStorage? recoveredPrimary = null;
		try
		{
			ThrowIfDisposed();
			if (Volatile.Read(ref _initialized))
				return;

			await DurableStorage.InitializeAsync(cancellationToken);
			recoveredPrimary = CreatePrimaryStorage();
			await recoveredPrimary.InitializeAsync(cancellationToken);

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
					);
					recoveredJobs.AddRange(jobs);

					var standaloneJobs = jobs
						.Where(static job => job.BatchHandle is null)
						.Select(static job => job.JobHandle)
						.ToArray();

					if (standaloneJobs.Length != 0)
					{
						// Standalone continuation edges are not represented by a batch graph, so recovery must
						// load them explicitly before it can restore dependency-gated jobs into the primary queue.
						var incomingEdges = await _graphDurableReplica.GetIncomingEdgesAsync(
							standaloneJobs,
							cancellationToken
						);

						foreach (var edge in incomingEdges)
						{
							if (!standaloneJobs.Contains(edge.ChildJobHandle))
							{
								throw new ImmediateJobException(
									$"Durable storage returned an incoming edge for unrequested job '{edge.ChildJobHandle}'."
								);
							}

							if (!recoveredIncomingEdges.TryGetValue(edge.ChildJobHandle, out var edges))
								recoveredIncomingEdges.Add(edge.ChildJobHandle, edges = []);
							edges.Add(edge);
						}
					}

					if (jobs.Count < RecoveryBatchSize)
						break;
					skip += jobs.Count;
				}
			}

			var batchHandles = recoveredJobs
				.Where(static job => job.BatchHandle is not null)
				.Select(static job => job.BatchHandle!)
				.Distinct()
				.ToArray();

			var recoveredBatches = new Dictionary<BatchHandle, RecoveredBatch>(batchHandles.Length);

			foreach (var batchHandle in batchHandles)
			{
				var status = await _graphDurableStorage.GetBatchStatusAsync(batchHandle, cancellationToken)
					?? throw new ImmediateJobException($"Batch '{batchHandle}' has members but no durable batch header.");
				var graph = await _graphDurableStorage.GetBatchGraphAsync(batchHandle, cancellationToken)
					?? throw new ImmediateJobException($"Batch '{batchHandle}' has members but no durable dependency graph.");

				recoveredBatches.Add(batchHandle, new RecoveredBatch
				{
					Record = new()
					{
						BatchHandle = status.BatchHandle,
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
					Jobs = [.. recoveredJobs.Where(job => job.BatchHandle == batchHandle)],
					Edges = [.. graph.Edges.Select(ToContinuationEdge)],
				});
			}

			var restoredBatchHandles = new HashSet<BatchHandle>();
			while (recoveredBatches.Count != 0)
			{
				var ready = recoveredBatches.Values
					.Where(batch => batch.Edges
						.Where(static edge => edge.ParentBatchHandle is not null)
						.All(edge => restoredBatchHandles.Contains(edge.ParentBatchHandle!)))
					.OrderBy(static batch => batch.Record.CreatedAt)
					.ThenBy(static batch => batch.Record.BatchHandle)
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
					);
					_ = recoveredBatches.Remove(batch.Record.BatchHandle);
					_ = restoredBatchHandles.Add(batch.Record.BatchHandle);
				}
			}

			var restoredJobHandles = recoveredJobs
				.Where(static job => job.BatchHandle is not null)
				.Select(static job => job.JobHandle)
				.ToHashSet();

			var allRecoveredJobHandles = recoveredJobs.Select(static job => job.JobHandle).ToHashSet();
			var standaloneContinuations = new Dictionary<JobHandle, JobRecord>();

			foreach (var job in recoveredJobs.Where(static job => job.BatchHandle is null))
			{
				if (!recoveredIncomingEdges.TryGetValue(job.JobHandle, out var incomingEdges))
				{
					if (job.State == JobState.AwaitingContinuation || job.RemainingDependencies != 0)
					{
						throw new ImmediateJobException(
							$"Job '{job.JobHandle}' has continuation dependencies but no durable dependency graph."
						);
					}

					await recoveredPrimary.EnqueueAsync(job, cancellationToken);
					_ = restoredJobHandles.Add(job.JobHandle);
				}
				else
				{
					standaloneContinuations.Add(job.JobHandle, job);
				}
			}

			bool AreContinuationParentsRestored(JobRecord job) => recoveredIncomingEdges[job.JobHandle].All(edge =>
				(edge.ParentJobHandle is null || restoredJobHandles.Contains(edge.ParentJobHandle))
				&& (edge.ParentBatchHandle is null || restoredBatchHandles.Contains(edge.ParentBatchHandle))
			);

			while (standaloneContinuations.Count != 0)
			{
				var ready = standaloneContinuations.Values
					.Where(AreContinuationParentsRestored)
					.OrderBy(static job => job.CreatedAt)
					.ThenBy(static job => job.JobHandle)
					.ToArray();

				if (ready.Length == 0)
				{
					var missingParents = standaloneContinuations.Values
						.SelectMany(job => recoveredIncomingEdges[job.JobHandle])
						.Where(edge => edge.ParentJobHandle is { } parentId && !allRecoveredJobHandles.Contains(parentId))
						.Select(static edge => edge.ParentJobHandle!)
						.Distinct()
						.Order()
						.ToList();

					var missingParentBatches = standaloneContinuations.Values
						.SelectMany(job => recoveredIncomingEdges[job.JobHandle])
						.Where(edge => edge.ParentBatchHandle is { } parentId && !restoredBatchHandles.Contains(parentId))
						.Select(static edge => edge.ParentBatchHandle!)
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
						recoveredIncomingEdges[job.JobHandle],
						cancellationToken
					);
					_ = standaloneContinuations.Remove(job.JobHandle);
					_ = restoredJobHandles.Add(job.JobHandle);
				}
			}

			var snapshot = await DurableStorage.GetMonitoringSnapshotAsync(cancellationToken);
			foreach (var schedule in snapshot.Recurring)
				await recoveredPrimary.UpsertRecurringAsync(schedule, cancellationToken);

			var previousPrimary = _primary;
			_primary = recoveredPrimary;
			recoveredPrimary = null;
			await previousPrimary.DisposeAsync();
			Volatile.Write(ref _initialized, true);
		}
		finally
		{
			if (recoveredPrimary is not null)
				await recoveredPrimary.DisposeAsync();
			_ = _initialization.Release();
		}
	}

	private void ThrowIfDisposed() =>
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

	private static JobContinuationEdge ToContinuationEdge(BatchGraphEdge edge) =>
		new()
		{
			ChildJobHandle = edge.ChildJobHandle,
			ParentJobHandle = edge.ParentJobHandle,
			ParentBatchHandle = edge.ParentBatchHandle,
			Trigger = edge.Trigger,
			Delay = edge.Delay,
		};

	private InMemoryJobStorage CreatePrimaryStorage() => new(_timeProvider);

	private sealed record RecoveredBatch
	{
		public required BatchRecord Record { get; init; }
		public required IReadOnlyList<JobRecord> Jobs { get; init; }
		public required IReadOnlyList<JobContinuationEdge> Edges { get; init; }
	}
}

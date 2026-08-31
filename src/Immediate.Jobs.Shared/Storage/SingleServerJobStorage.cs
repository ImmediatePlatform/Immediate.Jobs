using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
///		A single-server storage topology that executes against an authoritative in-process store while
///		synchronously replicating changes to durable storage and restoring them when the process starts.
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
internal sealed class SingleServerJobStorage(
	IJobStorage durableStorage,
	TimeProvider timeProvider
) :
	IRecurringJobStorage,
	IJobGraphStorage,
	IFairQueueStorage,
	IAsyncDisposable
{
	private const int RecoveryBatchSize = 1000;

	private readonly TaskCompletionSource _initializationTask = new();
	private bool _initialized;

	private readonly SemaphoreSlim _recurringMaterialization = new(1, 1);

	private bool _disposed;

	private InMemoryJobStorage PrimaryStorage { get; } = new(timeProvider);

	private IJobStorage DurableStorage { get; } =
		durableStorage switch
		{
			SingleServerJobStorage =>
				throw new ArgumentException("A single-server store cannot be used as its own durable replica.", nameof(durableStorage)),

			InMemoryJobStorage =>
				throw new ArgumentException("An in-memory store cannot be used as a durable replica.", nameof(durableStorage)),

			not IJobStorageReplica =>
				throw new ArgumentException("Single-server durable storage must implement IJobStorageReplica.", nameof(durableStorage)),

			not IRecurringJobStorage =>
				throw new ArgumentException("Single-server durable storage must support recurring jobs.", nameof(durableStorage)),

			not IJobGraphStorage =>
				throw new ArgumentException("Single-server durable storage must support batches and continuations.", nameof(durableStorage)),

			not IJobGraphStorageReplica =>
				throw new ArgumentException("Single-server durable storage must implement IJobGraphStorageReplica.", nameof(durableStorage)),

			_ => durableStorage,
		};

	private IRecurringJobStorage RecurringJobStorage => (IRecurringJobStorage)DurableStorage;
	private IJobGraphStorage JobGraphStorage => (IJobGraphStorage)DurableStorage;
	private IJobStorageReplica JobStorageReplica => (IJobStorageReplica)DurableStorage;
	private IJobGraphStorageReplica JobGraphStorageReplica => (IJobGraphStorageReplica)DurableStorage;

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await InitializeCoreAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.EnqueueAsync(job, cancellationToken);
		await PrimaryStorage.EnqueueAsync(job, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await JobGraphStorage.EnqueueContinuationAsync(job, edges, cancellationToken);
		await PrimaryStorage.EnqueueContinuationAsync(job, edges, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await JobGraphStorage.EnqueueBatchAsync(batch, jobs, edges, cancellationToken);
		await PrimaryStorage.EnqueueBatchAsync(batch, jobs, edges, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		var acquired = await PrimaryStorage
			.AcquireDueJobsAsync(request, cancellationToken);

		if (acquired.Count == 0)
			return acquired;

		var replicated = await JobStorageReplica
			.AcquireJobsAsync(
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
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await DurableStorage
			.SetExecutionTelemetryAsync(
				jobHandle,
				executionNumber,
				workerId,
				traceId,
				spanId,
				startedAt,
				cancellationToken
			);

		await PrimaryStorage
			.SetExecutionTelemetryAsync(
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
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await DurableStorage.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);
		await PrimaryStorage.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await DurableStorage.CompleteAsync(jobHandle, executionNumber, workerId, cancellationToken);
		await PrimaryStorage.CompleteAsync(jobHandle, executionNumber, workerId, cancellationToken);
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
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await JobGraphStorage
			.CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, additions, cancellationToken);

		await PrimaryStorage
			.CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, additions, cancellationToken);
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
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await JobGraphStorage.AddBatchJobAsync(currentJobHandle, executionNumber, job, options, cancellationToken);
		await PrimaryStorage.AddBatchJobAsync(currentJobHandle, executionNumber, job, options, cancellationToken);
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
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.FailAsync(jobHandle, executionNumber, workerId, error, nextRetryAt, cancellationToken);
		await PrimaryStorage.FailAsync(jobHandle, executionNumber, workerId, error, nextRetryAt, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await RecurringJobStorage.UpsertRecurringAsync(schedule, cancellationToken);
		await PrimaryStorage.UpsertRecurringAsync(schedule, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await RecurringJobStorage
			.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken);

		await PrimaryStorage
			.RemoveObsoleteCodeDefinedRecurringAsync(activeScheduleNames, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await RecurringJobStorage.RemoveRecurringAsync(name, cancellationToken);
		await PrimaryStorage.RemoveRecurringAsync(name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await RecurringJobStorage.PauseRecurringAsync(name, cancellationToken);
		await PrimaryStorage.PauseRecurringAsync(name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await RecurringJobStorage.ResumeRecurringAsync(name, cancellationToken);
		await PrimaryStorage.ResumeRecurringAsync(name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetDueRecurringAsync(now, batchSize, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await _recurringMaterialization.WaitAsync(cancellationToken);

		try
		{
			var durableResult = await RecurringJobStorage
				.MaterializeRecurringAsync(schedule, job, nextRunAt, cancellationToken);

			var primaryResult = await PrimaryStorage
				.MaterializeRecurringAsync(schedule, job, nextRunAt, cancellationToken);

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
			_recurringMaterialization.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetMonitoringSnapshotAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.QueryJobsAsync(query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobHandle,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
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
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetBatchStatusAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.QueryBatchesAsync(query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		BatchHandle batchHandle,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.QueryBatchMembersAsync(batchHandle, query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetBatchGraphAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobHandle,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetJobStatusAsync(jobHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await JobGraphStorage.CancelBatchAsync(batchHandle, cancellationToken);
		await PrimaryStorage.CancelBatchAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await JobGraphStorage.DeleteBatchAsync(batchHandle, cancellationToken);
		await PrimaryStorage.DeleteBatchAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.CancelAsync(jobHandle, cancellationToken);
		await PrimaryStorage.CancelAsync(jobHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.RetryAsync(jobHandle, cancellationToken);
		await PrimaryStorage.RetryAsync(jobHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.DeleteAsync(jobHandle, cancellationToken);
		await PrimaryStorage.DeleteAsync(jobHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await DurableStorage
			.PurgeJobsAsync(
				succeededRetention,
				failedRetention,
				cancellationToken
			);

		await PrimaryStorage
			.PurgeJobsAsync(
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
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await JobGraphStorage
			.PurgeBatchesAsync(
				batchSucceededRetention,
				batchFailedRetention,
				cancellationToken
			);

		await PrimaryStorage
			.PurgeBatchesAsync(
				batchSucceededRetention,
				batchFailedRetention,
				cancellationToken
			);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await PrimaryStorage.HeartbeatAsync(server, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await DurableStorage.IsHealthyAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		await TaskScheduler.Yield();
		if (_disposed)
			return;

		_disposed = true;
		_initializationTask.TrySetException(new ObjectDisposedException(nameof(SingleServerJobStorage)));

		_recurringMaterialization.Dispose();

		await PrimaryStorage.DisposeAsync();
		await DurableStorage.DisposeAsync();
	}

	private async Task EnsureInitializedAsync(CancellationToken token)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _initializationTask.Task.WaitAsync(token);
	}

	private async Task InitializeCoreAsync(CancellationToken cancellationToken)
	{
		if (_initialized)
			return;

		try
		{
			await DurableStorage.InitializeAsync(cancellationToken);
			await PrimaryStorage.InitializeAsync(cancellationToken);

			var recoveredJobs = new List<JobRecord>();
			var recoveredIncomingEdges = new Dictionary<JobHandle, List<JobContinuationEdge>>();

			foreach (var state in Enum.GetValues<JobState>())
			{
				var skip = 0;

				while (true)
				{
					var jobs = await DurableStorage
						.QueryJobsAsync(
							new() { State = state, Skip = skip, Take = RecoveryBatchSize },
							cancellationToken
						);

					recoveredJobs.AddRange(jobs);

					var standaloneJobs = jobs
						.Where(static job => job.BatchHandle is null)
						.Select(static job => job.JobHandle)
						.ToList();

					if (standaloneJobs.Count != 0)
					{
						// Standalone continuation edges are not represented by a batch graph, so recovery must
						// load them explicitly before it can restore dependency-gated jobs into the primary queue.
						var incomingEdges = await JobGraphStorageReplica
							.GetIncomingEdgesAsync(
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
				.ToList();

			var recoveredBatches = new Dictionary<BatchHandle, RecoveredBatch>(batchHandles.Count);

			foreach (var batchHandle in batchHandles)
			{
				var status = await JobGraphStorage.GetBatchStatusAsync(batchHandle, cancellationToken)
					?? throw new ImmediateJobException($"Batch '{batchHandle}' has members but no durable batch header.");
				var graph = await JobGraphStorage.GetBatchGraphAsync(batchHandle, cancellationToken)
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
					.ToList();

				if (ready.Count == 0)
				{
					var unresolved = string.Join(", ", recoveredBatches.Keys.Order());
					throw new ImmediateJobException(
						$"Durable batches have cyclic or missing parent-batch dependencies: {unresolved}."
					);
				}

				foreach (var batch in ready)
				{
					await PrimaryStorage
						.EnqueueBatchAsync(
							batch.Record,
							batch.Jobs,
							batch.Edges,
							cancellationToken
						);

					recoveredBatches.Remove(batch.Record.BatchHandle);
					restoredBatchHandles.Add(batch.Record.BatchHandle);
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

					await PrimaryStorage.EnqueueAsync(job, cancellationToken);
					restoredJobHandles.Add(job.JobHandle);
				}
				else
				{
					standaloneContinuations.Add(job.JobHandle, job);
				}
			}

			bool AreContinuationParentsRestored(JobRecord job) =>
				recoveredIncomingEdges[job.JobHandle]
					.TrueForAll(edge =>
						(edge.ParentJobHandle is null || restoredJobHandles.Contains(edge.ParentJobHandle))
						&& (edge.ParentBatchHandle is null || restoredBatchHandles.Contains(edge.ParentBatchHandle))
					);

			while (standaloneContinuations.Count != 0)
			{
				var ready = standaloneContinuations.Values
					.Where(AreContinuationParentsRestored)
					.OrderBy(static job => job.CreatedAt)
					.ThenBy(static job => job.JobHandle)
					.ToList();

				if (ready.Count == 0)
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
					await PrimaryStorage
						.EnqueueContinuationAsync(
							job,
							recoveredIncomingEdges[job.JobHandle],
							cancellationToken
						);

					standaloneContinuations.Remove(job.JobHandle);
					restoredJobHandles.Add(job.JobHandle);
				}
			}

			var snapshot = await DurableStorage.GetMonitoringSnapshotAsync(cancellationToken);
			foreach (var schedule in snapshot.Recurring)
				await PrimaryStorage.UpsertRecurringAsync(schedule, cancellationToken);

			_initializationTask.SetResult();
			_initialized = true;
		}
		catch (Exception ex)
		{
			_initializationTask.SetException(ex);
			_initialized = true;

			throw;
		}
	}

	private static JobContinuationEdge ToContinuationEdge(BatchGraphEdge edge) =>
		new()
		{
			ChildJobHandle = edge.ChildJobHandle,
			ParentJobHandle = edge.ParentJobHandle,
			ParentBatchHandle = edge.ParentBatchHandle,
			Trigger = edge.Trigger,
			Delay = edge.Delay,
		};

	private sealed record RecoveredBatch
	{
		public required BatchRecord Record { get; init; }
		public required IReadOnlyList<JobRecord> Jobs { get; init; }
		public required IReadOnlyList<JobContinuationEdge> Edges { get; init; }
	}
}

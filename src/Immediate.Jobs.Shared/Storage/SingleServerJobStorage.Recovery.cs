using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	private const int RecoveryBatchSize = 1000;

	private readonly TaskCompletionSource _initializationTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private int _initializationStarted;

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		SingleServerInitializeAsyncCalled();
		await TaskScheduler.Yield();
		await InitializeCoreAsync(cancellationToken);
	}

	private async Task EnsureInitializedAsync(CancellationToken token)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _initializationTask.Task.WaitAsync(token);
	}

	private async Task InitializeCoreAsync(CancellationToken cancellationToken)
	{
		if (Interlocked.CompareExchange(ref _initializationStarted, 1, 0) != 0)
		{
			await _initializationTask.Task.WaitAsync(cancellationToken);
			return;
		}

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
						var incomingEdges = await JobGraphStorage
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
					Edges = [.. graph.Edges],
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

			_ = _initializationTask.TrySetResult();
		}
		catch (Exception ex)
		{
			_ = _initializationTask.TrySetException(ex);

			throw;
		}
	}

	private sealed record RecoveredBatch
	{
		public required BatchRecord Record { get; init; }
		public required IReadOnlyList<JobRecord> Jobs { get; init; }
		public required IReadOnlyList<JobContinuationEdge> Edges { get; init; }
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerInitializeAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerInitializeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage InitializeAsync called"
	)]
	private partial void SingleServerInitializeAsyncCalled();
}

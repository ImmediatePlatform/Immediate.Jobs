using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		EnqueueAsyncCalled(job.JobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryAdd(job.JobHandle, job))
				throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
		}
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		EnqueueContinuationAsyncCalled(job.JobHandle, edges.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			ValidateNewJob(job);
			ValidateEdges([job], edges, batchHandle: null);

			var restoreExistingState = HasTerminalParent(edges) &&
				(job.State is not (JobState.AwaitingContinuation or JobState.WaitingForTrigger) || job.RemainingDependencies < edges.Count);
			_jobs.Add(job.JobHandle, restoreExistingState ? job : NormalizeWaitingJob(job, edges.Count));
			_edges.AddRange(edges);
			if (restoreExistingState)
				MarkTerminalParentEdgesSettled(edges);
			else
				EvaluateAlreadyTerminalParents(edges);
		}
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		EnqueueBatchAsyncCalled(batch.BatchHandle, jobs.Count, edges.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			ValidateBatch(batch, jobs, edges);

			var restoreExistingState = IsRecoveredBatch(batch, jobs, edges);
			_batches.Add(batch.BatchHandle, batch);

			var incomingCounts = edges
				.GroupBy(static edge => edge.ChildJobHandle)
				.ToDictionary(static group => group.Key, static group => group.Count());

			foreach (var job in jobs)
			{
				_jobs.Add(
					job.JobHandle,
					restoreExistingState
						? job
						: incomingCounts.TryGetValue(job.JobHandle, out var dependencyCount)
						? NormalizeWaitingJob(job, dependencyCount)
						: job with { BatchHandle = batch.BatchHandle, RemainingDependencies = 0, FailedDependencies = 0 }
				);
			}

			_edges.AddRange(edges);
			if (restoreExistingState)
				MarkTerminalParentEdgesSettled(edges);
			else
				EvaluateAlreadyTerminalParents(edges);
		}
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
		AddBatchJobAsyncCalled(job.JobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(currentJobHandle, out var current) || current.State != JobState.Active)
				throw new ImmediateJobException($"Job '{currentJobHandle}' is not currently active.");
			if (current.Attempt != executionNumber)
			{
				throw new ImmediateJobException(string.Create(
					CultureInfo.InvariantCulture,
					$"Execution {executionNumber} cannot add a batch job for '{currentJobHandle}'; the active execution is {current.Attempt}."
				));
			}

			if (current.BatchHandle is null || !_batches.ContainsKey(current.BatchHandle))
				throw new ImmediateJobException("The current job does not belong to a batch.");
			if (options == ContinuationOptions.Detached)
				throw new ImmediateJobException("AddToBatchAsync(JobDetails, ...) cannot create detached work.");
			if (options is not (ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations))
				throw new ArgumentOutOfRangeException(nameof(options));
			ValidateNewJob(job);
			if (job.BatchHandle != current.BatchHandle)
				throw new ImmediateJobException("The new job must belong to the current job's batch.");
			if (job.State is JobState.Active or JobState.AwaitingContinuation || IsTerminal(job.State))
				throw new ImmediateJobException($"Concurrent batch member '{job.JobHandle}' has invalid state '{job.State}'.");

			var existingWaiters = options == ContinuationOptions.BeforeContinuations
				? GetUnsettledWaiters(currentJobHandle)
				: [];
			ValidateSplice(existingWaiters, options == ContinuationOptions.BeforeContinuations ? 1 : 0);
			IncrementBatchMembers(current.BatchHandle, 1);
			_jobs.Add(job.JobHandle, job with { RemainingDependencies = 0 });
			if (options == ContinuationOptions.BeforeContinuations)
				SpliceBeforeWaiters(job.JobHandle, existingWaiters);
		}
	}

	private void ValidateNewJob(JobRecord job)
	{
		if (_jobs.ContainsKey(job.JobHandle))
			throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
	}

	private void ValidateBatch(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges
	)
	{
		if (_batches.ContainsKey(batch.BatchHandle))
			throw new ImmediateJobException($"Batch '{batch.BatchHandle}' already exists.");
		if (jobs.Count == 0)
			throw new ImmediateJobException("An atomic batch cannot be empty.");

		var succeeded = jobs.Count(static job => job.State == JobState.Succeeded);
		var failed = jobs.Count(static job => job.State == JobState.Failed);
		var cancelled = jobs.Count(static job => job.State == JobState.Cancelled);
		var skipped = jobs.Count(static job => job.State == JobState.Skipped);
		var pending = jobs.Count - succeeded - failed - cancelled - skipped;

		var expectedState = true switch
		{
			_ when pending != 0 => BatchState.Executing,
			_ when failed != 0 => BatchState.Failed,
			_ when cancelled != 0 => BatchState.Cancelled,
			_ => BatchState.Succeeded,
		};

		if (
			batch.TotalJobs != jobs.Count ||
			batch.PendingCount != pending ||
			batch.SucceededCount != succeeded ||
			batch.FailedCount != failed ||
			batch.CancelledCount != cancelled ||
			batch.SkippedCount != skipped ||
			(batch.State != expectedState && !(expectedState == BatchState.Executing && batch.State == BatchState.WaitingForTrigger)) ||
			(pending == 0 != (batch.CompletedAt is not null))
		)
		{
			throw new ImmediateJobException("A batch header does not match its members or aggregate state.");
		}

		var jobHandles = new HashSet<JobHandle>();
		foreach (var job in jobs)
		{
			ValidateNewJob(job);
			if (!jobHandles.Add(job.JobHandle))
				throw new ImmediateJobException($"Job '{job.JobHandle}' occurs more than once in the batch.");
			if (job.BatchHandle != batch.BatchHandle)
				throw new ImmediateJobException($"Job '{job.JobHandle}' does not belong to batch '{batch.BatchHandle}'.");
		}

		ValidateEdges(jobs, edges, batch.BatchHandle);
	}

	private void ValidateEdges(
		IReadOnlyList<JobRecord> newJobs,
		IReadOnlyList<JobContinuationEdge> edges,
		BatchHandle? batchHandle
	)
	{
		if (batchHandle is null && edges.Count == 0)
			throw new ImmediateJobException("A continuation must have at least one parent.");

		var newJobHandles = newJobs.Select(static job => job.JobHandle).ToHashSet();
		var logicalEdges = new HashSet<(JobHandle ChildJobHandle, string ParentKind, ContinuationHandle ParentJobHandle)>();

		var outgoing = newJobHandles.ToDictionary(static id => id, static _ => new List<JobHandle>());
		var incoming = newJobHandles.ToDictionary(static id => id, static _ => 0);

		foreach (var edge in edges)
		{
			if (!Enum.IsDefined(edge.Trigger))
				throw new ArgumentOutOfRangeException(nameof(edges), "Unknown continuation trigger.");
			if (!newJobHandles.Contains(edge.ChildJobHandle))
				throw new ImmediateJobException($"Continuation child '{edge.ChildJobHandle}' is not part of the atomic insert.");

			var hasJobParent = edge.ParentJobHandle is { };
			var hasBatchParent = edge.ParentBatchHandle is { };
			if (hasJobParent == hasBatchParent)
				throw new ImmediateJobException("A continuation edge must have exactly one job or batch parent.");

			if (hasJobParent)
			{
				var parentId = edge.ParentJobHandle!;
				if (parentId == edge.ChildJobHandle)
					throw new ImmediateJobException($"Continuation job '{edge.ChildJobHandle}' cannot depend on itself.");
				if (!newJobHandles.Contains(parentId) && !_jobs.ContainsKey(parentId))
					throw new KeyNotFoundException($"Continuation parent job '{parentId}' was not found.");
				if (!logicalEdges.Add((edge.ChildJobHandle, "job", parentId)))
					throw new ImmediateJobException($"Duplicate continuation edge '{parentId}' -> '{edge.ChildJobHandle}'.");

				if (newJobHandles.Contains(parentId))
				{
					outgoing[parentId].Add(edge.ChildJobHandle);
					incoming[edge.ChildJobHandle]++;
				}
			}
			else
			{
				var parentId = edge.ParentBatchHandle!;
				if (!_batches.ContainsKey(parentId))
					throw new KeyNotFoundException($"Continuation parent batch '{parentId}' was not found.");
				if (!logicalEdges.Add((edge.ChildJobHandle, "batch", parentId)))
					throw new ImmediateJobException($"Duplicate continuation edge from batch '{parentId}' to '{edge.ChildJobHandle}'.");
			}
		}

		var ready = new Queue<JobHandle>(incoming.Where(static pair => pair.Value == 0).Select(static pair => pair.Key));
		var visited = 0;
		while (ready.TryDequeue(out var parentId))
		{
			visited++;
			foreach (var childId in outgoing[parentId])
			{
				if (--incoming[childId] == 0)
					ready.Enqueue(childId);
			}
		}

		if (visited != newJobHandles.Count)
			throw new ImmediateJobException("The continuation graph contains a dependency cycle.");
	}

	private bool HasTerminalParent(IEnumerable<JobContinuationEdge> edges) =>
		edges.Any(IsTerminal);

	private bool IsTerminal(JobContinuationEdge edge)
	{
		return (
			edge.ParentJobHandle is { } parentJobHandle
			&& _jobs.TryGetValue(parentJobHandle, out var parentJob)
			&& IsTerminal(parentJob.State)
		) || (
			edge.ParentBatchHandle is { } parentBatchHandle
			&& _batches.TryGetValue(parentBatchHandle, out var parentBatch)
			&& IsTerminal(parentBatch.State)
		);
	}

	private bool IsRecoveredBatch(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges
	)
	{
		if (batch.StartedAt is not null ||
			batch.CompletedAt is not null ||
			batch.SucceededCount != 0 ||
			batch.FailedCount != 0 ||
			batch.CancelledCount != 0 ||
			batch.SkippedCount != 0 ||
			jobs.Any(static job => job.State is JobState.Active or JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped))
		{
			return true;
		}

		if (!HasTerminalParent(edges))
			return false;

		var incomingCounts = edges
			.GroupBy(static edge => edge.ChildJobHandle)
			.ToDictionary(static group => group.Key, static group => group.Count());
		return jobs.Any(job => incomingCounts.TryGetValue(job.JobHandle, out var incoming) &&
			(job.State is not (JobState.AwaitingContinuation or JobState.WaitingForTrigger) || job.RemainingDependencies < incoming));
	}

	private void MarkTerminalParentEdgesSettled(IEnumerable<JobContinuationEdge> edges)
	{
		foreach (var edge in edges)
		{
			if (IsTerminal(edge))
			{
				_ = _settledEdges.Add(edge);
			}
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryEnqueueAsyncCalled,
		EventName = "Immediate.Jobs.Shared.EnqueueAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueAsync called (JobHandle={JobHandle})"
	)]
	private partial void EnqueueAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryEnqueueContinuationAsyncCalled,
		EventName = "Immediate.Jobs.Shared.EnqueueContinuationAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueContinuationAsync called (JobHandle={JobHandle}, Edges={Edges})"
	)]
	private partial void EnqueueContinuationAsyncCalled(JobHandle jobHandle, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryEnqueueBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.EnqueueBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueBatchAsync called (BatchHandle={BatchHandle}, Jobs={Jobs}, Edges={Edges})"
	)]
	private partial void EnqueueBatchAsyncCalled(BatchHandle batchHandle, int jobs, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryAddBatchJobAsyncCalled,
		EventName = "Immediate.Jobs.Shared.AddBatchJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AddBatchJobAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void AddBatchJobAsyncCalled(JobHandle jobHandle, int execution);
}

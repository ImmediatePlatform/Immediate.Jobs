using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		CompleteAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, [], cancellationToken);
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
		CompleteWithContinuationsAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var current = GetOwnedActive(jobHandle, executionNumber, workerId);
			var existingWaiters = GetUnsettledWaiters(jobHandle);
			var newJobHandles = new HashSet<JobHandle>();
			var dependencyEdges = new List<JobContinuationEdge>(additions.Count);
			var trackedAdditions = 0;

			foreach (var addition in additions)
			{
				ValidateNewJob(addition.Job);
				if (!newJobHandles.Add(addition.Job.JobHandle))
					throw new ImmediateJobException($"Job '{addition.Job.JobHandle}' occurs more than once in the completion buffer.");
				if (addition.Job.State is not (JobState.Pending or JobState.Scheduled))
					throw new ImmediateJobException($"Dynamic continuation '{addition.Job.JobHandle}' has invalid state '{addition.Job.State}'.");
				if (!Enum.IsDefined(addition.Trigger))
					throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation trigger.");

				if (addition.Options == ContinuationOptions.Detached)
				{
					if (addition.Job.BatchHandle is not null)
						throw new ImmediateJobException("A detached continuation cannot belong to a batch.");
				}
				else if (addition.Options is ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations)
				{
					if (current.BatchHandle is null || addition.Job.BatchHandle != current.BatchHandle)
						throw new ImmediateJobException("A batch-tracked continuation must belong to the current job's batch.");
					trackedAdditions++;
				}
				else
				{
					throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation option.");
				}

				dependencyEdges.Add(new()
				{
					ChildJobHandle = addition.Job.JobHandle,
					ParentJobHandle = jobHandle,
					Trigger = addition.Trigger,
					Delay = addition.Delay,
				});
			}

			if (dependencyEdges.Count != 0)
				ValidateEdges([.. additions.Select(static addition => addition.Job)], dependencyEdges, current.BatchHandle);

			ValidateSplice(existingWaiters, additions.Count(static addition => addition.Options == ContinuationOptions.BeforeContinuations));

			IncrementBatchMembers(current.BatchHandle, trackedAdditions);
			foreach (var addition in additions)
			{
				_jobs.Add(addition.Job.JobHandle, NormalizeWaitingJob(addition.Job, dependencyCount: 1));
				if (addition.Options == ContinuationOptions.BeforeContinuations)
					SpliceBeforeWaiters(addition.Job.JobHandle, existingWaiters);
			}

			_edges.AddRange(dependencyEdges);
			var completedAt = timeProvider.GetUtcNow();
			CompleteExecution(current, JobExecutionState.Succeeded, completedAt);
			TransitionToTerminal(jobHandle, JobState.Succeeded, error: null, completedAt);
		}
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
		FailAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var job = GetOwnedActive(jobHandle, executionNumber, workerId);
			var completedAt = timeProvider.GetUtcNow();
			CompleteExecution(job, JobExecutionState.Failed, completedAt, error);
			if (nextRetryAt.HasValue)
			{
				var now = timeProvider.GetUtcNow();
				_jobs[jobHandle] = job with
				{
					State = nextRetryAt <= now ? JobState.Pending : JobState.Scheduled,
					DueAt = nextRetryAt.Value,
					WorkerId = null,
					LeaseExpiresAt = null,
					LastError = error,
					CompletedAt = null,
				};
			}
			else
			{
				TransitionToTerminal(jobHandle, JobState.Failed, error, completedAt);
			}
		}
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		CancelAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			if (IsTerminal(job.State))
				throw new ImmediateJobException("Only a non-terminal job can be cancelled.");

			var now = timeProvider.GetUtcNow();
			if (job.State == JobState.Active)
				CompleteExecution(job, JobExecutionState.Cancelled, now);
			TransitionToTerminal(jobHandle, JobState.Cancelled, error: null, now);
		}
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		CancelBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchHandle, out var batch))
				throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
			if (IsTerminal(batch.State))
				throw new ImmediateJobException("Only an executing batch can be cancelled.");

			var now = timeProvider.GetUtcNow();
			var jobHandles = _jobs.Values
				.Where(job => job.BatchHandle == batchHandle && !IsTerminal(job.State))
				.Select(static job => job.JobHandle)
				.ToList();
			foreach (var jobHandle in jobHandles)
			{
				if (_jobs.TryGetValue(jobHandle, out var job) && job.State == JobState.Active)
					CompleteExecution(job, JobExecutionState.Cancelled, now);
				TransitionToTerminal(jobHandle, JobState.Cancelled, error: null, now, propagateContinuations: false);
			}

			foreach (var jobHandle in jobHandles)
				ProcessTerminalJob(jobHandle);
		}
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		DeleteBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchHandle, out var batch))
				throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
			if (!IsTerminal(batch.State))
				throw new ImmediateJobException("Only a terminal batch can be deleted.");

			var jobHandles = _jobs.Values
				.Where(job => job.BatchHandle == batchHandle)
				.Select(static job => job.JobHandle)
				.ToHashSet();

			foreach (var jobHandle in jobHandles)
			{
				_ = _jobs.Remove(jobHandle);
				_ = _executions.Remove(jobHandle);
			}

			_ = _batches.Remove(batchHandle);
			RemoveEdgesForJobs(jobHandles, [batchHandle]);
		}
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		RetryAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			var wasFailed = job.State == JobState.Failed;
			if (!wasFailed && job.State != JobState.Scheduled)
				throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");

			MaterializeSyntheticExecution(job);
			_jobs[jobHandle] = job with
			{
				State = JobState.Pending,
				DueAt = timeProvider.GetUtcNow(),
				CompletedAt = wasFailed ? null : job.CompletedAt,
				LastError = wasFailed ? null : job.LastError,
			};
			if (wasFailed && job.BatchHandle is { } batchHandle && _batches.TryGetValue(batchHandle, out var batch))
			{
				_batches[batchHandle] = batch with
				{
					State = BatchState.Executing,
					PendingCount = batch.PendingCount + 1,
					FailedCount = Math.Max(0, batch.FailedCount - 1),
					CompletedAt = null,
				};
			}
		}
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		DeleteAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			if (!IsTerminal(job.State))
				throw new ImmediateJobException("Only terminal jobs can be deleted.");
			if (job.BatchHandle is not null)
				throw new ImmediateJobException("Batch members cannot be deleted individually.");

			_ = _jobs.Remove(jobHandle);
			_ = _executions.Remove(jobHandle);
			if (job.RecurringKey is { } recurringKey)
				_ = _recurringKeys.Remove(recurringKey);
			RemoveEdgesForJobs([jobHandle]);
		}
	}

	/// <inheritdoc />
	public async ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		PurgeJobsAsyncCalled(succeededRetention, failedRetention);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			var standaloneJobHandles = _jobs.Values
				.Where(static job => job.BatchHandle is null)
				.Where(
					x =>
						x.CompletedAt is { } completed
						&& (
							(x.State is JobState.Succeeded && completed < now - succeededRetention)
							|| (x.State is JobState.Failed or JobState.Cancelled or JobState.Skipped && completed < now - failedRetention)
						)
				)
				.Select(static job => job.JobHandle)
				.ToHashSet();

			foreach (var id in standaloneJobHandles)
			{
				if (_jobs[id].RecurringKey is { } recurringKey)
					_ = _recurringKeys.Remove(recurringKey);
				_ = _jobs.Remove(id);
				_ = _executions.Remove(id);
			}

			RemoveEdgesForJobs(standaloneJobHandles);
		}
	}

	/// <inheritdoc />
	public async ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		PurgeBatchesAsyncCalled(batchSucceededRetention, batchFailedRetention);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = timeProvider.GetUtcNow();
		lock (_gate)
		{
			var batchHandles = _batches.Values
				.Where(
					batch =>
						batch.CompletedAt is { } completed
						&& (
							(batch.State is BatchState.Succeeded && completed < now - batchSucceededRetention)
							|| (batch.State is BatchState.Failed or BatchState.Cancelled && completed < now - batchFailedRetention)
						)
				)
				.Select(static batch => batch.BatchHandle)
				.ToHashSet();

			var batchJobHandles = _jobs.Values
				.Where(job => job.BatchHandle is { } batchHandle && batchHandles.Contains(batchHandle))
				.Select(static job => job.JobHandle)
				.ToHashSet();

			foreach (var id in batchJobHandles)
			{
				_ = _jobs.Remove(id);
				_ = _executions.Remove(id);
			}

			foreach (var batchHandle in batchHandles)
				_ = _batches.Remove(batchHandle);
			RemoveEdgesForJobs(batchJobHandles, batchHandles);
		}
	}

	private void TransitionToTerminal(
		JobHandle jobHandle,
		JobState terminalState,
		string? error,
		DateTimeOffset completedAt,
		bool propagateContinuations = true
	)
	{
		if (!IsTerminal(terminalState))
			throw new ArgumentOutOfRangeException(nameof(terminalState));
		if (!_jobs.TryGetValue(jobHandle, out var job) || IsTerminal(job.State))
			return;

		_jobs[jobHandle] = job with
		{
			State = terminalState,
			WorkerId = null,
			LeaseExpiresAt = null,
			LastError = error,
			CompletedAt = completedAt,
		};
		UpdateBatchAfterTerminal(job.BatchHandle, terminalState, completedAt);
		if (propagateContinuations)
			ProcessTerminalJob(jobHandle);
		RemoveFairQueueCursorWhenBacklogClears(job.QueueName, job.GroupId);
	}

	private void RemoveFairQueueCursorWhenBacklogClears(string queueName, string? groupId)
	{
		if (groupId is null ||
			_jobs.Values.Any(job =>
				string.Equals(job.QueueName, queueName, StringComparison.Ordinal) &&
				string.Equals(job.GroupId, groupId, StringComparison.Ordinal) &&
				job.State is JobState.Pending or JobState.Scheduled or JobState.Active))
		{
			return;
		}

		_ = _fairQueueLastServed.Remove((queueName, groupId));
	}

	private void UpdateBatchAfterTerminal(BatchHandle? batchHandle, JobState state, DateTimeOffset completedAt)
	{
		if (batchHandle is null || !_batches.TryGetValue(batchHandle, out var batch))
			return;

		var pending = Math.Max(0, batch.PendingCount - 1);
		batch = batch with
		{
			PendingCount = pending,
			SucceededCount = batch.SucceededCount + (state == JobState.Succeeded ? 1 : 0),
			FailedCount = batch.FailedCount + (state == JobState.Failed ? 1 : 0),
			CancelledCount = batch.CancelledCount + (state == JobState.Cancelled ? 1 : 0),
			SkippedCount = batch.SkippedCount + (state == JobState.Skipped ? 1 : 0),
		};
		if (pending == 0)
		{
			batch = batch with
			{
				State = batch.FailedCount != 0
					? BatchState.Failed
					: batch.CancelledCount != 0 ? BatchState.Cancelled : BatchState.Succeeded,
				CompletedAt = completedAt,
			};
		}

		_batches[batchHandle] = batch;
		if (pending == 0)
			ProcessTerminalBatch(batchHandle);
	}

	private void RemoveEdgesForJobs(
		HashSet<JobHandle> jobHandles,
		HashSet<BatchHandle>? batchHandles = null
	)
	{
		if (jobHandles.Count == 0 && (batchHandles is null || batchHandles.Count == 0))
			return;

		var batches = batchHandles ?? [];
		for (var index = _edges.Count - 1; index >= 0; index--)
		{
			var edge = _edges[index];
			if (!jobHandles.Contains(edge.ChildJobHandle) &&
				(edge.ParentJobHandle is null || !jobHandles.Contains(edge.ParentJobHandle)) &&
				(edge.ParentBatchHandle is null || !batches.Contains(edge.ParentBatchHandle)))
			{
				continue;
			}

			_edges.RemoveAt(index);
			_ = _settledEdges.Remove(edge);
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryCompleteAsyncCalled,
		EventName = "Immediate.Jobs.Shared.CompleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryCompleteWithContinuationsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.CompleteWithContinuationsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteWithContinuationsAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteWithContinuationsAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryFailAsyncCalled,
		EventName = "Immediate.Jobs.Shared.FailAsyncCalled",
		Level = LogLevel.Debug,
		Message = "FailAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void FailAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryCancelBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.CancelBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void CancelBatchAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryDeleteBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.DeleteBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DeleteBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void DeleteBatchAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryCancelAsyncCalled,
		EventName = "Immediate.Jobs.Shared.CancelAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelAsync called (JobHandle={JobHandle})"
	)]
	private partial void CancelAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryRetryAsyncCalled,
		EventName = "Immediate.Jobs.Shared.RetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RetryAsync called (JobHandle={JobHandle})"
	)]
	private partial void RetryAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryDeleteAsyncCalled,
		EventName = "Immediate.Jobs.Shared.DeleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DeleteAsync called (JobHandle={JobHandle})"
	)]
	private partial void DeleteAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryPurgeJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.PurgeJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeJobsAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeJobsAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryPurgeBatchesAsyncCalled,
		EventName = "Immediate.Jobs.Shared.PurgeBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeBatchesAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeBatchesAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);
}

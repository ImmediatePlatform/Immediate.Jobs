using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
			var records = additions.ToDictionary(static addition => addition.Job.JobHandle, addition => CreateRecord(addition.Job));
			var existingWaiters = GetUnsettledWaiters(jobHandle);
			var newJobHandles = new HashSet<JobHandle>();
			var dependencyEdges = new List<JobContinuationEdge>(additions.Count);
			var trackedAdditions = 0;

			foreach (var addition in additions)
			{
				ValidateNewJob(records[addition.Job.JobHandle]);
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
				ValidateEdges([.. records.Values], dependencyEdges, current.BatchHandle);

			ValidateSplice(existingWaiters, additions.Count(static addition => addition.Options == ContinuationOptions.BeforeContinuations));

			IncrementBatchMembers(current.BatchHandle, trackedAdditions);
			foreach (var addition in additions)
			{
				AddJob(NormalizeWaitingJob(records[addition.Job.JobHandle], dependencyCount: 1));
				if (addition.Options == ContinuationOptions.BeforeContinuations)
					SpliceBeforeWaiters(addition.Job.JobHandle, existingWaiters);
			}

			AddEdges(dependencyEdges);
			var completedAt = timeProvider.GetUtcNow();
			CompleteExecution(current, JobExecutionState.Succeeded, completedAt);
			// Continuation inserts above may resize _jobs; obtain the ref only after they finish.
			ref var completed = ref ValidateExistingElement(_jobs, jobHandle);
			TransitionToTerminal(ref completed, JobState.Succeeded, error: null, completedAt);
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
			ref var job = ref GetOwnedActive(jobHandle, executionNumber, workerId);
			var completedAt = timeProvider.GetUtcNow();
			CompleteExecution(job, JobExecutionState.Failed, completedAt, error);
			if (nextRetryAt.HasValue)
			{
				var now = timeProvider.GetUtcNow();
				UpdateJob(ref job, job with
				{
					State = nextRetryAt <= now ? JobState.Pending : JobState.Scheduled,
					DueAt = nextRetryAt.Value,
					WorkerId = null,
					LeaseExpiresAt = null,
					LastError = error,
					CompletedAt = null,
				});
			}
			else
			{
				TransitionToTerminal(ref job, JobState.Failed, error, completedAt);
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
			ref var job = ref CollectionsMarshal.GetValueRefOrNullRef(_jobs, jobHandle);
			if (Unsafe.IsNullRef(ref job))
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			if (IsTerminal(job.State))
				throw new ImmediateJobException("Only a non-terminal job can be cancelled.");

			var now = timeProvider.GetUtcNow();
			if (job.State == JobState.Active)
				CompleteExecution(job, JobExecutionState.Cancelled, now);
			TransitionToTerminal(ref job, JobState.Cancelled, error: null, now);
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
			ref var batch = ref ValidateExistingElement(_batches, batchHandle);

			if (IsTerminal(batch.State))
				throw new ImmediateJobException("Only an executing batch can be cancelled.");

			var now = timeProvider.GetUtcNow();
			var jobHandles = new List<JobHandle>();

			if (_jobsByBatch.TryGetValue(batchHandle, out var handles))
			{
				foreach (var handle in handles)
				{
					ref var job = ref ValidateExistingElement(_jobs, handle);

					if (IsTerminal(job.State))
						continue;

					if (job.State == JobState.Active)
						CompleteExecution(job, JobExecutionState.Cancelled, now);

					TransitionToTerminal(ref job, JobState.Cancelled, error: null, now, propagateContinuations: false);
					jobHandles.Add(handle);
				}
			}

			foreach (var jobHandle in jobHandles)
				ProcessTerminalJob(jobHandle);
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
			ref var job = ref CollectionsMarshal.GetValueRefOrNullRef(_jobs, jobHandle);
			if (Unsafe.IsNullRef(ref job))
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			var wasFailed = job.State == JobState.Failed;
			if (!wasFailed && job.State != JobState.Scheduled)
				throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");

			MaterializeSyntheticExecution(job);
			UpdateJob(ref job, job with
			{
				State = JobState.Pending,
				DueAt = timeProvider.GetUtcNow(),
				CompletedAt = wasFailed ? null : job.CompletedAt,
				LastError = wasFailed ? null : job.LastError,
			});
			if (wasFailed && job.BatchHandle is { } batchHandle)
			{
				ref var batch = ref CollectionsMarshal.GetValueRefOrNullRef(_batches, batchHandle);
				if (Unsafe.IsNullRef(ref batch))
					return;
				batch = batch with
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

			RemoveJobs(standaloneJobHandles);
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

			var batchJobHandles = new HashSet<JobHandle>();

			foreach (var batchHandle in batchHandles)
			{
				_batches.Remove(batchHandle);

				if (_jobsByBatch.Remove(batchHandle, out var handles))
				{
					foreach (var h in handles)
						batchJobHandles.Add(h);
				}
			}

			RemoveJobs(batchJobHandles);
			RemoveEdgesForJobs(batchJobHandles, batchHandles);
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void TransitionToTerminal(
		ref JobRecord job,
		JobState terminalState,
		string? error,
		DateTimeOffset completedAt,
		bool propagateContinuations = true
	)
	{
		if (!IsTerminal(terminalState))
			throw new ArgumentOutOfRangeException(nameof(terminalState));
		if (IsTerminal(job.State))
			return;

		var jobHandle = job.JobHandle;
		var batchHandle = job.BatchHandle;
		var queueName = GetQueueName(job.JobName);
		var groupId = job.GroupId;
		UpdateJob(ref job, job with
		{
			State = terminalState,
			WorkerId = null,
			LeaseExpiresAt = null,
			LastError = error,
			CompletedAt = completedAt,
		});
		UpdateBatchAfterTerminal(batchHandle, terminalState, completedAt);
		if (propagateContinuations)
			ProcessTerminalJob(jobHandle);
		RemoveFairQueueCursorWhenBacklogClears(queueName, groupId);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void RemoveFairQueueCursorWhenBacklogClears(string? queueName, string? groupId)
	{
		if (queueName is null || groupId is null ||
			GetJobsForQueue(queueName).Any(job =>
				string.Equals(job.GroupId, groupId, StringComparison.Ordinal) &&
				job.State is JobState.Pending or JobState.Scheduled or JobState.Active))
		{
			return;
		}

		_ = _fairQueueLastServed.Remove((queueName, groupId));
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void UpdateBatchAfterTerminal(BatchHandle? batchHandle, JobState state, DateTimeOffset completedAt)
	{
		if (batchHandle is null)
			return;

		ref var batch = ref CollectionsMarshal.GetValueRefOrNullRef(_batches, batchHandle);

		if (Unsafe.IsNullRef(ref batch))
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
				State = batch switch
				{
					{ FailedCount: not 0 } => BatchState.Failed,
					{ CancelledCount: not 0 } => BatchState.Cancelled,
					_ => BatchState.Succeeded,
				},
				CompletedAt = completedAt,
			};

			ProcessTerminalBatch(batch.BatchHandle);
		}
	}

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryCompleteAsyncCalled,
		EventName = "Immediate.Jobs.Shared.CompleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteAsyncCalled(JobHandle jobHandle, int execution);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryCompleteWithContinuationsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.CompleteWithContinuationsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteWithContinuationsAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteWithContinuationsAsyncCalled(JobHandle jobHandle, int execution);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryFailAsyncCalled,
		EventName = "Immediate.Jobs.Shared.FailAsyncCalled",
		Level = LogLevel.Debug,
		Message = "FailAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void FailAsyncCalled(JobHandle jobHandle, int execution);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryCancelBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.CancelBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void CancelBatchAsyncCalled(BatchHandle batchHandle);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryCancelAsyncCalled,
		EventName = "Immediate.Jobs.Shared.CancelAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelAsync called (JobHandle={JobHandle})"
	)]
	private partial void CancelAsyncCalled(JobHandle jobHandle);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryRetryAsyncCalled,
		EventName = "Immediate.Jobs.Shared.RetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RetryAsync called (JobHandle={JobHandle})"
	)]
	private partial void RetryAsyncCalled(JobHandle jobHandle);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryPurgeJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.PurgeJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeJobsAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeJobsAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryPurgeBatchesAsyncCalled,
		EventName = "Immediate.Jobs.Shared.PurgeBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeBatchesAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeBatchesAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);
}

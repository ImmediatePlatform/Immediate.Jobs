using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
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

		await MutateOwnedWithDependenciesAsync(
			jobHandle,
			executionNumber,
			workerId,
			error: null,
			nextRetryAt: null,
			succeeded: true,
			additions,
			cancellationToken
		);
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

		await MutateOwnedWithDependenciesAsync(
			jobHandle,
			executionNumber,
			workerId,
			error,
			nextRetryAt,
			succeeded: false,
			[],
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		CancelBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var terminalGroups = new HashSet<string>(StringComparer.Ordinal);
		await RetryConcurrencyAsync(
			connection => CancelBatchCoreAsync(
				connection,
				batchHandle,
				terminalGroups,
				cancellationToken
			),
			cancellationToken
		);
		await CleanupFairQueueGroupsAsync(terminalGroups);
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		CancelAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var terminalGroups = new HashSet<string>(StringComparer.Ordinal);
		await RetryConcurrencyAsync(
			connection => CancelCoreAsync(connection, jobHandle, terminalGroups, cancellationToken),
			cancellationToken
		);
		await CleanupFairQueueGroupsAsync(terminalGroups);
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		RetryAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(
			connection => RetryCoreAsync(connection, jobHandle, cancellationToken),
			cancellationToken
		);
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
		await RetryConcurrencyAsync(async connection =>
		{
			var jobs = await Jobs(connection)
				.Where(job =>
					job.BatchHandle == null
					&& (
						(
							job.State == JobState.Succeeded
							&& job.CompletedAt < (now - succeededRetention)
						) || (
							(job.State == JobState.Failed || job.State == JobState.Cancelled || job.State == JobState.Skipped)
							&& job.CompletedAt < (now - failedRetention)
						)
					)
				)
				.Select(job => new ImmediateJobEntity { Id = job.Id, ConcurrencyStamp = job.ConcurrencyStamp })
				.ToListAsync(cancellationToken);

			if (jobs.Count != 0)
			{
				foreach (var block in jobs.Chunk(256))
					if (await VersionedJobs(connection, block).Set(job => job.ConcurrencyStamp, Guid.NewGuid()).UpdateAsync(cancellationToken) != block.Length)
						throw new LostRaceException();
				var jobHandles = jobs.Select(static job => job.Id).ToList();
				_ = await Continuations(connection)
					.Where(edge =>
						jobHandles.Contains(edge.ChildJobHandle)
						|| (jobHandles.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
					)
					.DeleteAsync(cancellationToken);
				_ = await Executions(connection).Where(execution => jobHandles.Contains(execution.JobHandle))
					.DeleteAsync(cancellationToken);
				_ = await Jobs(connection).Where(job => jobHandles.Contains(job.Id)).DeleteAsync(cancellationToken);
			}
		}, cancellationToken);
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
		await RetryConcurrencyAsync(
			connection => PurgeBatchesCoreAsync(
				connection,
				now - batchSucceededRetention,
				now - batchFailedRetention,
				cancellationToken
			),
			cancellationToken
		);
	}

	private async Task CancelBatchCoreAsync(
		DataConnection connection,
		BatchHandle batchHandle,
		ISet<string> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
		if (IsTerminal(batch.State))
			throw new ImmediateJobException("Only an executing batch can be cancelled.");
		var jobsToCancel = await Jobs(connection)
			.Where(job => job.BatchHandle == batchHandle.Value && (job.State == JobState.AwaitingContinuation
				|| job.State == JobState.WaitingForTrigger || job.State == JobState.Scheduled
				|| job.State == JobState.Pending || job.State == JobState.Active))
			.OrderBy(job => job.Id).ToListAsync(cancellationToken);
		await MaterializeExecutionsAsync(connection, jobsToCancel.Where(static job => job.State == JobState.Active), cancellationToken);
		foreach (var block in jobsToCancel.Chunk(256))
		{
			if (block.Any(static job => job.State == JobState.Active && job.Attempt <= 0))
				throw new ImmediateJobException("An active batch member has no execution ordinal.");
			var activeIds = block.Where(static job => job.State == JobState.Active).Select(static job => job.Id).ToList();
			if (activeIds.Count != 0)
				await Executions(connection).Where(execution => Jobs(connection).Any(job => activeIds.Contains(job.Id)
					&& job.Id == execution.JobHandle && job.Attempt == execution.Attempt))
					.Set(execution => execution.State, JobExecutionState.Cancelled)
					.Set(execution => execution.CompletedAt, now)
					.Set(execution => execution.Error, (string?)null).UpdateAsync(cancellationToken);
			var stamp = Guid.NewGuid();
			var updated = await VersionedJobs(connection, block)
				.Set(job => job.State, JobState.Cancelled)
				.Set(job => job.CompletedAt, now)
				.Set(job => job.WorkerId, (string?)null)
				.Set(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
				.Set(job => job.ConcurrencyStamp, stamp).UpdateAsync(cancellationToken);
			if (updated != block.Length)
				throw new LostRaceException();
			foreach (var job in block)
			{
				job.State = JobState.Cancelled;
				job.CompletedAt = now;
				job.WorkerId = null;
				job.LeaseExpiresAt = null;
				job.ConcurrencyStamp = stamp;
			}
		}

		await PropagateTerminalAsync(connection, jobsToCancel, now, terminalGroups, cancellationToken);
	}

	private async Task CancelCoreAsync(
		DataConnection connection,
		JobHandle jobHandle,
		ISet<string> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (IsTerminal(job.State))
			throw new ImmediateJobException("Only a non-terminal job can be cancelled.");

		var now = timeProvider.GetUtcNow();
		await MarkCancelledAsync(connection, job, now, reason: null, cancellationToken);
		await PropagateTerminalAsync(connection, job, now, terminalGroups, cancellationToken);
	}

	private async Task MarkCancelledAsync(DataConnection connection, ImmediateJobEntity job, DateTimeOffset now, string? reason, CancellationToken cancellationToken, bool executionPrepared = false)
	{
		if (job.State == JobState.Active)
		{
			if (job.Attempt <= 0)
				throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
			if (!executionPrepared)
				await GetOrMaterializeExecutionAsync(connection, job, cancellationToken);
			_ = await Executions(connection)
				.Where(execution => execution.JobHandle == job.Id && execution.Attempt == job.Attempt)
				.Set(execution => execution.State, JobExecutionState.Cancelled)
				.Set(execution => execution.CompletedAt, now)
				.Set(execution => execution.Error, reason)
				.UpdateAsync(cancellationToken);
		}

		var oldStamp = job.ConcurrencyStamp;
		job.CancellationReason = reason;
		job.State = JobState.Cancelled;
		job.CompletedAt = now;
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
			throw new LostRaceException();
	}

	private async Task RetryCoreAsync(DataConnection connection, JobHandle jobHandle, CancellationToken cancellationToken)
	{
		var job = await Jobs(connection)
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value &&
				(item.State == JobState.Failed || item.State == JobState.Scheduled), cancellationToken);
		if (job is null)
		{
			if (await Jobs(connection).AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
				throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");
			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		}

		var wasFailed = job.State == JobState.Failed;
		if (wasFailed && job.BatchHandle is { } batchHandle)
		{
			var updated = await Batches(connection).Where(batch => batch.Id == batchHandle)
				.Set(batch => batch.PendingCount, batch => batch.PendingCount + 1)
				.Set(batch => batch.FailedCount, batch => batch.FailedCount > 0 ? batch.FailedCount - 1 : 0)
				.Set(batch => batch.State, BatchState.Executing)
				.Set(batch => batch.CompletedAt, (DateTimeOffset?)null)
				.Set(batch => batch.ConcurrencyStamp, Guid.NewGuid()).UpdateAsync(cancellationToken);
			if (updated == 0)
				throw new LostRaceException();
		}

		_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken);

		var oldStamp = job.ConcurrencyStamp;
		job.State = JobState.Pending;
		job.DueAt = timeProvider.GetUtcNow();
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		if (wasFailed)
		{
			job.CompletedAt = null;
			job.LastError = null;
		}

		job.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
			throw new LostRaceException();
	}

	private async Task PurgeBatchesCoreAsync(
		DataConnection connection,
		DateTimeOffset batchSucceededBefore,
		DateTimeOffset batchFailedBefore,
		CancellationToken cancellationToken
	)
	{
		var batches = await Batches(connection)
			.Where(batch =>
				(
					batch.State == BatchState.Succeeded
					&& batch.CompletedAt < batchSucceededBefore
				) || (
					(batch.State == BatchState.Failed || batch.State == BatchState.Cancelled)
					&& batch.CompletedAt < batchFailedBefore
				)
			)
			.OrderBy(batch => batch.Id)
			.ToListAsync(cancellationToken);
		if (batches.Count == 0)
			return;

		// Claim batches in ID order before touching dependent rows so retry and purge use the same lock order.
		foreach (var batch in batches)
		{
			var updated = await Batches(connection).Where(item => item.Id == batch.Id && item.ConcurrencyStamp == batch.ConcurrencyStamp)
				.Set(item => item.ConcurrencyStamp, Guid.NewGuid()).UpdateAsync(cancellationToken);
			if (updated == 0)
				throw new LostRaceException();
		}

		var batchHandles = batches.Select(static batch => batch.Id).ToList();
		var memberIds = Jobs(connection)
			.Where(job => job.BatchHandle != null && batchHandles.Contains(job.BatchHandle))
			.Select(job => job.Id);
		_ = await Continuations(connection)
			.Where(edge =>
				(batchHandles.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Batch)
				|| memberIds.Contains(edge.ChildJobHandle)
				|| (memberIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
			)
			.DeleteAsync(cancellationToken);
		_ = await Executions(connection).Where(execution => memberIds.Contains(execution.JobHandle))
			.DeleteAsync(cancellationToken);
		_ = await Jobs(connection).Where(job => job.BatchHandle != null && batchHandles.Contains(job.BatchHandle))
			.DeleteAsync(cancellationToken);
		_ = await Batches(connection).Where(batch => batchHandles.Contains(batch.Id)).DeleteAsync(cancellationToken);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.CompleteAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.CompleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.CompleteWithContinuationsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.CompleteWithContinuationsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteWithContinuationsAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteWithContinuationsAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.FailAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.FailAsyncCalled",
		Level = LogLevel.Debug,
		Message = "FailAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void FailAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.CancelBatchAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.CancelBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void CancelBatchAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.CancelAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.CancelAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelAsync called (JobHandle={JobHandle})"
	)]
	private partial void CancelAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.RetryAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.RetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RetryAsync called (JobHandle={JobHandle})"
	)]
	private partial void RetryAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.PurgeJobsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.PurgeJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeJobsAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeJobsAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);

	[LoggerMessage(
		EventId = LibraryEventIds.PurgeBatchesAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.PurgeBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeBatchesAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeBatchesAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);
}

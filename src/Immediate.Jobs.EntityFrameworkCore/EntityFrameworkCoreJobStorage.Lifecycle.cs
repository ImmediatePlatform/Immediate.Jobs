using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
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

		await RetryConcurrencyAsync(
			operationCancellationToken => CancelBatchCoreAsync(batchHandle, operationCancellationToken),
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		DeleteBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await ExecuteWithStrategyAsync(
			operationCancellationToken => DeleteBatchCoreAsync(batchHandle, operationCancellationToken),
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		CancelAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(
			operationCancellationToken => CancelCoreAsync(jobHandle, operationCancellationToken),
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		RetryAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(
			operationCancellationToken => RetryCoreAsync(jobHandle, operationCancellationToken),
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		DeleteAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await ExecuteWithStrategyAsync(
			operationCancellationToken => DeleteCoreAsync(jobHandle, operationCancellationToken),
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

		var now = _timeProvider.GetUtcNow();
		await ExecuteWithStrategyAsync(
			operationCancellationToken => PurgeJobsCoreAsync(
				now - succeededRetention,
				now - failedRetention,
				operationCancellationToken
			),
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
		PurgeBatchesAsyncCalled(batchSucceededRetention, batchFailedRetention);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = _timeProvider.GetUtcNow();
		await RetryConcurrencyAsync(
			operationCancellationToken => PurgeBatchesCoreAsync(
				now - batchSucceededRetention,
				now - batchFailedRetention,
				operationCancellationToken
			),
			cancellationToken
		);
	}

	private async Task CancelBatchCoreAsync(BatchHandle batchHandle, CancellationToken cancellationToken)
	{
		var now = _timeProvider.GetUtcNow();
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
		if (IsTerminal(batch.State))
			throw new ImmediateJobException("Only an executing batch can be cancelled.");

		var jobs = await context.Set<ImmediateJobEntity>()
			.Where(job => job.BatchHandle == batchHandle.Value)
			.ToListAsync(cancellationToken);
		var jobsToCancel = jobs.Where(job => !IsTerminal(job.State)).ToList();
		foreach (var job in jobsToCancel)
		{
			if (job.State == JobState.Active)
			{
				var execution = await GetOrMaterializeExecutionAsync(context, job, cancellationToken)
					?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
				execution.State = JobExecutionState.Cancelled;
				execution.CompletedAt = now;
				execution.Error = null;
			}

			job.State = JobState.Cancelled;
			job.CompletedAt = now;
			job.WorkerId = null;
			job.LeaseExpiresAt = null;
			job.ConcurrencyStamp = Guid.NewGuid();
		}

		foreach (var job in jobsToCancel)
			await PropagateTerminalAsync(context, job, now, cancellationToken);

		var terminalGroups = GetTerminalFairQueueGroups(context);
		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		foreach (var (queueName, groupId) in terminalGroups)
		{
			await TryRemoveFairQueueCursorAsync(queueName, groupId, CancellationToken.None);
		}
	}

	private async Task DeleteBatchCoreAsync(BatchHandle batchHandle, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
		if (!IsTerminal(batch.State))
			throw new ImmediateJobException("Only a terminal batch can be deleted.");

		var jobs = await context.Set<ImmediateJobEntity>()
			.Where(job => job.BatchHandle == batchHandle.Value)
			.ToListAsync(cancellationToken);
		var jobHandles = jobs.Select(static job => job.Id).ToList();
		var edges = await context.Set<ImmediateJobContinuationEntity>()
			.Where(edge =>
				jobHandles.Contains(edge.ChildJobHandle)
				|| (edge.ParentKind == ContinuationParentKind.Job && jobHandles.Contains(edge.ParentId))
				|| (edge.ParentKind == ContinuationParentKind.Batch && edge.ParentId == batchHandle.Value))
			.ToListAsync(cancellationToken);
		context.RemoveRange(edges);
		context.RemoveRange(jobs);
		_ = context.Remove(batch);
		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
	}

	private async Task CancelCoreAsync(JobHandle jobHandle, CancellationToken cancellationToken)
	{
		var now = _timeProvider.GetUtcNow();
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (IsTerminal(job.State))
			throw new ImmediateJobException("Only a non-terminal job can be cancelled.");

		if (job.State == JobState.Active)
		{
			var execution = await GetOrMaterializeExecutionAsync(context, job, cancellationToken)
				?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
			execution.State = JobExecutionState.Cancelled;
			execution.CompletedAt = now;
			execution.Error = null;
		}

		job.State = JobState.Cancelled;
		job.CompletedAt = now;
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.ConcurrencyStamp = Guid.NewGuid();
		await PropagateTerminalAsync(context, job, now, cancellationToken);

		var terminalGroups = GetTerminalFairQueueGroups(context);
		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		foreach (var (queueName, groupId) in terminalGroups)
		{
			await TryRemoveFairQueueCursorAsync(queueName, groupId, CancellationToken.None);
		}
	}

	private async Task RetryCoreAsync(JobHandle jobHandle, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value &&
				(item.State == JobState.Failed || item.State == JobState.Scheduled), cancellationToken);
		if (job is null)
		{
			if (await context.Set<ImmediateJobEntity>()
				.AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
			{
				throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");
			}

			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		}

		var wasFailed = job.State == JobState.Failed;
		_ = await GetOrMaterializeExecutionAsync(context, job, cancellationToken);
		if (wasFailed && job.BatchHandle is { } batchHandle)
		{
			var batch = await context.Set<ImmediateJobBatchEntity>()
				.SingleOrDefaultAsync(item => item.Id == batchHandle, cancellationToken)
				?? throw new DbUpdateConcurrencyException();
			batch.PendingCount++;
			batch.FailedCount = Math.Max(0, batch.FailedCount - 1);
			batch.State = BatchState.Executing;
			batch.CompletedAt = null;
			batch.ConcurrencyStamp = Guid.NewGuid();
		}

		job.State = JobState.Pending;
		job.DueAt = _timeProvider.GetUtcNow();
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		if (wasFailed)
		{
			job.CompletedAt = null;
			job.LastError = null;
		}

		job.ConcurrencyStamp = Guid.NewGuid();
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken);
		}
		catch (DbUpdateConcurrencyException ex)
		{
			if (!await context.Set<ImmediateJobEntity>()
				.AsNoTracking()
				.AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
			{
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.", ex);
			}

			throw new ImmediateJobException("Only failed or scheduled jobs can be retried.", ex);
		}

		await transaction.CommitAsync(cancellationToken);
	}

	private async Task DeleteCoreAsync(JobHandle jobHandle, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var job = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value
				&& (item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled || item.State == JobState.Skipped), cancellationToken);
		if (job is null)
		{
			if (await context.Set<ImmediateJobEntity>()
				.AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
			{
				throw new ImmediateJobException("Only terminal jobs can be deleted.");
			}

			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		}

		if (job.BatchHandle is not null)
			throw new ImmediateJobException("Batch members are deleted with their batch so the workflow remains coherent.");
		_ = await context.Set<ImmediateJobContinuationEntity>()
			.Where(edge => edge.ChildJobHandle == jobHandle.Value ||
				(edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == jobHandle.Value))
			.ExecuteDeleteAsync(cancellationToken);
		var removed = await context.Set<ImmediateJobEntity>()
			.Where(item => item.Id == jobHandle.Value &&
				(item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled || item.State == JobState.Skipped))
			.ExecuteDeleteAsync(cancellationToken);
		if (removed == 0)
		{
			if (await context.Set<ImmediateJobEntity>()
				.AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
			{
				throw new ImmediateJobException("Only terminal jobs can be deleted.");
			}

			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		}

		await transaction.CommitAsync(cancellationToken);
	}

	private async Task PurgeJobsCoreAsync(
		DateTimeOffset succeededBefore,
		DateTimeOffset failedBefore,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var jobs = await context.Set<ImmediateJobEntity>()
			.Where(job => job.BatchHandle == null
				&& ((job.State == JobState.Succeeded && job.CompletedAt < succeededBefore)
				|| ((job.State == JobState.Failed || job.State == JobState.Cancelled || job.State == JobState.Skipped) && job.CompletedAt < failedBefore))
			)
			.ToListAsync(cancellationToken);
		if (jobs.Count != 0)
		{
			var jobHandles = jobs.Select(static job => job.Id).ToList();
			var edges = await context.Set<ImmediateJobContinuationEntity>()
				.Where(edge =>
					jobHandles.Contains(edge.ChildJobHandle)
					|| (jobHandles.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
				)
				.ToListAsync(cancellationToken);
			context.RemoveRange(edges);
		}

		context.RemoveRange(jobs);
		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
	}

	private async Task PurgeBatchesCoreAsync(
		DateTimeOffset batchSucceededBefore,
		DateTimeOffset batchFailedBefore,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var batches = await context.Set<ImmediateJobBatchEntity>()
			.Where(batch => (batch.State == BatchState.Succeeded && batch.CompletedAt < batchSucceededBefore)
				|| ((batch.State == BatchState.Failed || batch.State == BatchState.Cancelled)
					&& batch.CompletedAt < batchFailedBefore))
			.ToListAsync(cancellationToken);
		if (batches.Count != 0)
		{
			var batchHandles = batches.Select(static batch => batch.Id).ToList();
			var memberIds = await context.Set<ImmediateJobEntity>()
				.Where(job => job.BatchHandle != null && batchHandles.Contains(job.BatchHandle))
				.Select(job => job.Id)
				.ToListAsync(cancellationToken);
			_ = await context.Set<ImmediateJobContinuationEntity>()
				.Where(edge =>
					(batchHandles.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Batch)
					|| memberIds.Contains(edge.ChildJobHandle)
					|| (memberIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
				)
				.ExecuteDeleteAsync(cancellationToken);
			// A concurrent retry changes the batch stamp, causing SaveChanges to roll back the edge deletion.
			context.RemoveRange(batches);
		}

		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.CompleteAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.CompleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.CompleteWithContinuationsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.CompleteWithContinuationsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteWithContinuationsAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteWithContinuationsAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.FailAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.FailAsyncCalled",
		Level = LogLevel.Debug,
		Message = "FailAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void FailAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.CancelBatchAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.CancelBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void CancelBatchAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.DeleteBatchAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.DeleteBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DeleteBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void DeleteBatchAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.CancelAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.CancelAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelAsync called (JobHandle={JobHandle})"
	)]
	private partial void CancelAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.RetryAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.RetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RetryAsync called (JobHandle={JobHandle})"
	)]
	private partial void RetryAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.DeleteAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.DeleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DeleteAsync called (JobHandle={JobHandle})"
	)]
	private partial void DeleteAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.PurgeJobsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.PurgeJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeJobsAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeJobsAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);

	[LoggerMessage(
		EventId = LibraryEventIds.PurgeBatchesAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.PurgeBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeBatchesAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeBatchesAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);
}

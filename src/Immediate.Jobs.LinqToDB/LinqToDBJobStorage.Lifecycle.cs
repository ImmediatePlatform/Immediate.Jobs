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

		var terminalGroups = new HashSet<(string QueueName, string GroupId)>();
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
	public async ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		DeleteBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		_ = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken)
				?? throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
			if (!IsTerminal(batch.State))
				throw new ImmediateJobException("Only a terminal batch can be deleted.");
			var jobHandles = await Jobs(connection).Where(job => job.BatchHandle == batchHandle.Value).Select(job => job.Id)
				.ToListAsync(cancellationToken);
			_ = await Continuations(connection)
				.Where(edge =>
					jobHandles.Contains(edge.ChildJobHandle)
					|| (edge.ParentKind == ContinuationParentKind.Job && jobHandles.Contains(edge.ParentId))
					|| (edge.ParentKind == ContinuationParentKind.Batch && edge.ParentId == batchHandle.Value)
				)
				.DeleteAsync(cancellationToken);
			_ = await Executions(connection).Where(execution => jobHandles.Contains(execution.JobHandle))
				.DeleteAsync(cancellationToken);
			_ = await Jobs(connection).Where(job => job.BatchHandle == batchHandle.Value).DeleteAsync(cancellationToken);
			_ = await Batches(connection).Where(item => item.Id == batchHandle.Value).DeleteAsync(cancellationToken);
			await connection.CommitTransactionAsync(cancellationToken);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			throw;
		}
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		CancelAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var terminalGroups = new HashSet<(string QueueName, string GroupId)>();
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
	public async ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		DeleteAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		_ = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle.Value &&
				(item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled || item.State == JobState.Skipped), cancellationToken);
			if (job is null)
			{
				if (await Jobs(connection).AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
					throw new ImmediateJobException("Only terminal jobs can be deleted.");
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			}

			if (job.BatchHandle is not null)
				throw new ImmediateJobException("Batch members are deleted with their batch so the workflow remains coherent.");
			_ = await Continuations(connection)
				.Where(edge => edge.ChildJobHandle == jobHandle.Value ||
					(edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == jobHandle.Value))
				.DeleteAsync(cancellationToken);
			_ = await Executions(connection).Where(execution => execution.JobHandle == jobHandle.Value)
				.DeleteAsync(cancellationToken);
			var removed = await Jobs(connection)
				.Where(item => item.Id == jobHandle.Value &&
					(item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled || item.State == JobState.Skipped))
				.DeleteAsync(cancellationToken);
			if (removed == 0)
			{
				if (await Jobs(connection).AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
					throw new ImmediateJobException("Only terminal jobs can be deleted.");
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			}

			await connection.CommitTransactionAsync(cancellationToken);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			throw;
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

		await using var scope = contextScope.GetScope(out var connection);

		var now = timeProvider.GetUtcNow();
		_ = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			var jobHandles = await Jobs(connection)
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
				.Select(job => job.Id)
				.ToListAsync(cancellationToken);

			if (jobHandles.Count != 0)
			{
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

			await connection.CommitTransactionAsync(cancellationToken);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			throw;
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
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
		if (IsTerminal(batch.State))
			throw new ImmediateJobException("Only an executing batch can be cancelled.");
		var jobHandles = await Jobs(connection).Where(job => job.BatchHandle == batchHandle.Value).Select(job => job.Id)
			.ToListAsync(cancellationToken);
		var jobsToCancel = new List<ImmediateJobEntity>(jobHandles.Count);
		foreach (var jobHandle in jobHandles)
		{
			var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle, cancellationToken);
			if (job is null || IsTerminal(job.State))
				continue;
			if (job.State == JobState.Active)
			{
				_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken)
					?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
				_ = await Executions(connection)
					.Where(execution => execution.JobHandle == job.Id && execution.Attempt == job.Attempt)
					.Set(execution => execution.State, JobExecutionState.Cancelled)
					.Set(execution => execution.CompletedAt, now)
					.Set(execution => execution.Error, (string?)null)
						.UpdateAsync(cancellationToken);
			}

			var oldStamp = job.ConcurrencyStamp;
			job.State = JobState.Cancelled;
			job.CompletedAt = now;
			job.WorkerId = null;
			job.LeaseExpiresAt = null;
			job.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
				throw new LostRaceException();
			jobsToCancel.Add(job);
		}

		foreach (var job in jobsToCancel)
		{
			await PropagateTerminalAsync(
				connection,
				job,
				now,
				terminalGroups,
				cancellationToken
			);
		}
	}

	private async Task CancelCoreAsync(
		DataConnection connection,
		JobHandle jobHandle,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (IsTerminal(job.State))
			throw new ImmediateJobException("Only a non-terminal job can be cancelled.");

		var now = timeProvider.GetUtcNow();
		if (job.State == JobState.Active)
		{
			_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken)
				?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
			_ = await Executions(connection)
				.Where(execution => execution.JobHandle == job.Id && execution.Attempt == job.Attempt)
				.Set(execution => execution.State, JobExecutionState.Cancelled)
				.Set(execution => execution.CompletedAt, now)
				.Set(execution => execution.Error, (string?)null)
				.UpdateAsync(cancellationToken);
		}

		var oldStamp = job.ConcurrencyStamp;
		job.State = JobState.Cancelled;
		job.CompletedAt = now;
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
			throw new LostRaceException();
		await PropagateTerminalAsync(connection, job, now, terminalGroups, cancellationToken);
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
			var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle, cancellationToken)
				?? throw new LostRaceException();
			var batchStamp = batch.ConcurrencyStamp;
			batch.PendingCount++;
			batch.FailedCount = Math.Max(0, batch.FailedCount - 1);
			batch.State = BatchState.Executing;
			batch.CompletedAt = null;
			batch.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken))
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
			var oldStamp = batch.ConcurrencyStamp;
			batch.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateBatchAsync(connection, batch, oldStamp, cancellationToken))
				throw new LostRaceException();
		}

		var batchHandles = batches.Select(static batch => batch.Id).ToList();
		var memberIds = await Jobs(connection)
			.Where(job => job.BatchHandle != null && batchHandles.Contains(job.BatchHandle))
			.Select(job => job.Id)
			.ToListAsync(cancellationToken);
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
		EventId = LibraryEventIds.DeleteBatchAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.DeleteBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DeleteBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void DeleteBatchAsyncCalled(BatchHandle batchHandle);

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
		EventId = LibraryEventIds.DeleteAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.DeleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DeleteAsync called (JobHandle={JobHandle})"
	)]
	private partial void DeleteAsyncCalled(JobHandle jobHandle);

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

using Immediate.Jobs.Shared.Apis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	/// <inheritdoc />
	public async ValueTask UpdatePayloadAsync(
		JobHandle jobHandle,
		string expectedJobName,
		string payload,
		CancellationToken cancellationToken = default
	)
	{
		UpdatePayloadAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(
			operationCancellationToken => UpdatePayloadCoreAsync(jobHandle, expectedJobName, payload, operationCancellationToken),
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask<bool> TryTriggerAsync(
		JobHandle jobHandle,
		string expectedJobName,
		DateTimeOffset dueAt,
		CancellationToken cancellationToken = default
	)
	{
		TryTriggerAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var triggered = false;
		await RetryConcurrencyAsync(
			async operationCancellationToken =>
				triggered = await TryTriggerCoreAsync(jobHandle, expectedJobName, dueAt, operationCancellationToken),
			cancellationToken
		);
		return triggered;
	}

	/// <inheritdoc />
	public async ValueTask<bool> TryTriggerBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		TryTriggerBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var triggered = false;
		await RetryConcurrencyAsync(
			async operationCancellationToken =>
				triggered = await TryTriggerBatchCoreAsync(batchHandle, operationCancellationToken),
			cancellationToken
		);
		return triggered;
	}

	private async Task UpdatePayloadCoreAsync(
		JobHandle jobHandle,
		string expectedJobName,
		string payload,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var job = await GetTriggerableAsync(context, jobHandle, expectedJobName, cancellationToken);
		if (job.State != JobState.WaitingForTrigger)
			throw new ImmediateJobException($"Job '{jobHandle}' is not waiting for a trigger.");

		job.Payload = payload;
		job.ConcurrencyStamp = Guid.NewGuid();
		_ = await context.SaveChangesAsync(cancellationToken);
	}

	private async Task<bool> TryTriggerCoreAsync(
		JobHandle jobHandle,
		string expectedJobName,
		DateTimeOffset dueAt,
		CancellationToken cancellationToken
	)
	{
		var now = _timeProvider.GetUtcNow();
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var job = await GetTriggerableAsync(context, jobHandle, expectedJobName, cancellationToken);
		if (job.BatchHandle is not null && await context.Set<ImmediateJobBatchEntity>()
			.AnyAsync(batch => batch.Id == job.BatchHandle && batch.State == BatchState.WaitingForTrigger, cancellationToken))
		{
			throw new ImmediateJobException($"Job '{jobHandle}' belongs to batch '{job.BatchHandle}', which is waiting for a trigger; trigger the batch instead.");
		}

		if (job.State != JobState.WaitingForTrigger)
			return false;
		if (job.Payload.Length == 0)
			throw new ImmediateJobException($"Job '{jobHandle}' cannot be triggered before its parameters are supplied.");

		await TriggerWaitingJobAsync(context, job, dueAt, now, cancellationToken);

		var terminalGroups = GetTerminalFairQueueGroups(context);
		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		foreach (var (queueName, groupId) in terminalGroups)
		{
			await TryRemoveFairQueueCursorAsync(queueName, groupId, CancellationToken.None);
		}

		return true;
	}

	private static async Task<ImmediateJobEntity> GetTriggerableAsync(
		TContext context,
		JobHandle jobHandle,
		string expectedJobName,
		CancellationToken cancellationToken
	)
	{
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (!string.Equals(job.JobName, expectedJobName, StringComparison.Ordinal))
			throw new ImmediateJobException($"Job '{jobHandle}' is not a '{expectedJobName}' job.");

		return job;
	}

	private static async Task TriggerWaitingJobAsync(
		TContext context,
		ImmediateJobEntity job,
		DateTimeOffset dueAt,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		job.DueAt = dueAt;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (job.RemainingDependencies != 0)
		{
			job.State = JobState.AwaitingContinuation;
			return;
		}

		if (await ShouldSkipSettledContinuationAsync(context, job.Id, cancellationToken))
		{
			job.State = JobState.Skipped;
			job.CompletedAt = now;
			await PropagateTerminalAsync(context, job, now, cancellationToken);
			return;
		}

		var delayedDueAt = now + await GetMaximumContinuationDelayAsync(context, job.Id, cancellationToken);
		if (job.DueAt < delayedDueAt)
			job.DueAt = delayedDueAt;
		job.State = job.DueAt <= now ? JobState.Pending : JobState.Scheduled;
	}

	private async Task<bool> TryTriggerBatchCoreAsync(BatchHandle batchHandle, CancellationToken cancellationToken)
	{
		var now = _timeProvider.GetUtcNow();
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
		if (batch.State != BatchState.WaitingForTrigger)
			return false;

		batch.State = BatchState.Executing;
		batch.ConcurrencyStamp = Guid.NewGuid();
		var jobs = await context.Set<ImmediateJobEntity>()
			.Where(job => job.BatchHandle == batchHandle.Value && job.State == JobState.WaitingForTrigger)
			.OrderBy(static job => job.Id)
			.ToListAsync(cancellationToken);
		foreach (var job in jobs)
			await TriggerWaitingJobAsync(context, job, job.DueAt, now, cancellationToken);

		var terminalGroups = GetTerminalFairQueueGroups(context);
		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		foreach (var (queueName, groupId) in terminalGroups)
		{
			await TryRemoveFairQueueCursorAsync(queueName, groupId, CancellationToken.None);
		}

		return true;
	}

	[LoggerMessage(
		EventId = LibraryEventIds.UpdatePayloadAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.UpdatePayloadAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpdatePayloadAsync called (JobHandle={JobHandle})"
	)]
	private partial void UpdatePayloadAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.TryTriggerAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.TryTriggerAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerAsync called (JobHandle={JobHandle})"
	)]
	private partial void TryTriggerAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.TryTriggerBatchAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.TryTriggerBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void TryTriggerBatchAsyncCalled(BatchHandle batchHandle);
}

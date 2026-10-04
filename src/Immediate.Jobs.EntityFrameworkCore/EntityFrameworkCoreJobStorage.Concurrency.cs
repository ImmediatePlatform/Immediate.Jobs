using System.Data.Common;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	private const int MaxConcurrencyAttempts = 5;

	private ValueTask MutateOwnedWithDependenciesAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string? error,
		DateTimeOffset? nextRetryAt,
		bool succeeded,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken
	) => RetryConcurrencyAsync(
		operationCancellationToken => MutateOwnedCoreAsync(
			jobHandle,
			executionNumber,
			workerId,
			error,
			nextRetryAt,
			succeeded,
			additions,
			operationCancellationToken
		),
		cancellationToken
	);

	private async ValueTask RetryConcurrencyAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken
	)
	{
		var concurrencyAttempt = 0;
		while (true)
		{
			try
			{
				await ExecuteWithStrategyAsync(operation, cancellationToken);
				return;
			}
			catch (DbUpdateConcurrencyException) when (++concurrencyAttempt < MaxConcurrencyAttempts)
			{
				// The transaction rolled back, so the next attempt re-evaluates the graph from durable state.
			}
			catch (DbUpdateException exception) when (++concurrencyAttempt < MaxConcurrencyAttempts)
			{
				if (!await IsSyntheticExecutionInsertRaceAsync(exception, cancellationToken))
					throw;
				// The failed context is disposed by the operation; retry with the execution inserted by the winner.
			}
		}
	}

	private async ValueTask ExecuteWithStrategyAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken
	)
	{
		await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
		var strategy = strategyContext.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(operation, cancellationToken);
	}

	private async ValueTask<bool> IsSyntheticExecutionInsertRaceAsync(
		DbUpdateException exception,
		CancellationToken cancellationToken
	)
	{
		var syntheticExecutions = exception.Entries
			.Select(static entry => entry.Entity)
			.OfType<ImmediateJobExecutionEntity>()
			.Where(static execution => execution.IsSynthetic)
			.DistinctBy(static execution => (execution.JobHandle, execution.Attempt))
			.ToList();
		if (syntheticExecutions.Count == 0)
			return false;

		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			foreach (var execution in syntheticExecutions)
			{
				if (!await context.Set<ImmediateJobExecutionEntity>()
					.AsNoTracking()
					.AnyAsync(
						item => item.JobHandle == execution.JobHandle && item.Attempt == execution.Attempt,
						cancellationToken
					))
				{
					return false;
				}
			}

			return true;
		}
		catch (Exception verificationException) when (
			verificationException is DbException or InvalidOperationException
		)
		{
			return false;
		}
	}

	private ValueTask MutateOwnedAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		Action<ImmediateJobEntity, ImmediateJobExecutionEntity> mutate,
		CancellationToken cancellationToken
	) => RetryConcurrencyAsync(
		operationCancellationToken => MutateOwnedOnceAsync(
			jobHandle,
			executionNumber,
			workerId,
			mutate,
			operationCancellationToken
		),
		cancellationToken
	);

	private async Task MutateOwnedOnceAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		Action<ImmediateJobEntity, ImmediateJobExecutionEntity> mutate,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value && item.Attempt == executionNumber && item.State == JobState.Active && item.WorkerId == workerId, cancellationToken) ?? throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
		var execution = await GetOrMaterializeExecutionAsync(context, job, cancellationToken)
			?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
		mutate(job, execution);
		job.ConcurrencyStamp = Guid.NewGuid();
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken);
		}
		catch (DbUpdateConcurrencyException exception)
		{
			throw new ImmediateJobException(
				$"Worker '{workerId}' does not own active job '{jobHandle}'.",
				exception
			);
		}
	}

	private async Task MutateOwnedCoreAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string? error,
		DateTimeOffset? nextRetryAt,
		bool succeeded,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value && item.Attempt == executionNumber && item.State == JobState.Active && item.WorkerId == workerId, cancellationToken) ?? throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
		var now = _timeProvider.GetUtcNow();
		var execution = await GetOrMaterializeExecutionAsync(context, job, cancellationToken)
			?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
		execution.State = succeeded ? JobExecutionState.Succeeded : JobExecutionState.Failed;
		execution.CompletedAt = now;
		execution.Error = error;
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.LastError = error;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!succeeded && nextRetryAt is { } retryAt)
		{
			job.State = retryAt <= now ? JobState.Pending : JobState.Scheduled;
			job.DueAt = retryAt;
			job.CompletedAt = null;
			_ = await context.SaveChangesAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return;
		}

		if (succeeded && additions.Count != 0)
		{
			await FlushContinuationAdditionsAsync(context, job, additions, cancellationToken);
			_ = await context.SaveChangesAsync(cancellationToken);
		}

		job.State = succeeded ? JobState.Succeeded : JobState.Failed;
		job.CompletedAt = now;
		await PropagateTerminalAsync(context, job, now, cancellationToken);
		var terminalGroups = GetTerminalFairQueueGroups(context);
		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		foreach (var (queueName, groupId) in terminalGroups)
			await TryRemoveFairQueueCursorAsync(queueName, groupId, CancellationToken.None);
	}
}

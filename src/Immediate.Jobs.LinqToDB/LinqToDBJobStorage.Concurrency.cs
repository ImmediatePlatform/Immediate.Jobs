using System.Data.Common;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	private const int MaxContendedCompletionAttempts = 50;

	private const int MaxConcurrencyAttempts = 5;

	private async ValueTask MutateOwnedWithDependenciesAsync(
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
		var terminalGroups = new HashSet<(string QueueName, string GroupId)>();
		await RetryConcurrencyAsync(
			connection => MutateOwnedCoreAsync(
				connection,
				jobHandle,
				executionNumber,
				workerId,
				error,
				nextRetryAt,
				succeeded,
				additions,
				terminalGroups,
				cancellationToken
			),
			cancellationToken,
			maxAttempts: MaxContendedCompletionAttempts
		);
		await CleanupFairQueueGroupsAsync(terminalGroups);
	}

	private async Task MutateOwnedCoreAsync(
		DataConnection connection,
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string? error,
		DateTimeOffset? nextRetryAt,
		bool succeeded,
		IReadOnlyList<JobContinuationAddition> additions,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var job = await Jobs(connection)
			.SingleOrDefaultAsync(
				item => item.Id == jobHandle.Value && item.Attempt == executionNumber && item.State == JobState.Active && item.WorkerId == workerId,
				cancellationToken
			) ?? throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
		var oldStamp = job.ConcurrencyStamp;
		var now = timeProvider.GetUtcNow();
		_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken)
			?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
		var executionUpdated = await Executions(connection)
			.Where(execution => execution.JobHandle == jobHandle.Value && execution.Attempt == executionNumber && execution.State == JobExecutionState.Active)
			.Set(execution => execution.State, succeeded ? JobExecutionState.Succeeded : JobExecutionState.Failed)
			.Set(execution => execution.CompletedAt, now)
			.Set(execution => execution.Error, error)
			.UpdateAsync(cancellationToken);
		if (executionUpdated == 0)
			throw new LostRaceException();
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.LastError = error;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!succeeded && nextRetryAt is { } retryAt)
		{
			job.State = retryAt <= now ? JobState.Pending : JobState.Scheduled;
			job.DueAt = retryAt;
			job.CompletedAt = null;
			if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
				throw new LostRaceException();
			return;
		}

		if (succeeded && additions.Count != 0)
			await FlushContinuationAdditionsAsync(connection, job, additions, cancellationToken);
		job.State = succeeded ? JobState.Succeeded : JobState.Failed;
		job.CompletedAt = now;
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
			throw new LostRaceException();
		await PropagateTerminalAsync(
			connection,
			job,
			now,
			terminalGroups,
			cancellationToken
		);
	}

	private async ValueTask RetryConcurrencyAsync(
		Func<T, Task> operation,
		CancellationToken cancellationToken,
		int maxAttempts = MaxConcurrencyAttempts
	)
	{
		var concurrencyAttempt = 0;
		while (true)
		{
			await using var scope = contextScope.GetScope(out var connection);

			_ = await connection.BeginTransactionAsync(cancellationToken);
			try
			{
				await operation(connection);
				await connection.CommitTransactionAsync(cancellationToken);
				return;
			}
			catch (SyntheticExecutionInsertFailedException exception) when (++concurrencyAttempt < maxAttempts)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				if (!await SyntheticExecutionExistsAsync(exception.JobHandle, exception.Attempt, cancellationToken))
				{
					throw exception.DatabaseException;
				}
				// Retry in a new transaction and re-read the execution inserted by the winner.
				await DelayConcurrencyRetryAsync(cancellationToken);
			}
			catch (LostRaceException) when (++concurrencyAttempt < maxAttempts)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				await DelayConcurrencyRetryAsync(cancellationToken);
			}
			catch (SyntheticExecutionInsertFailedException exception)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				if (!await SyntheticExecutionExistsAsync(exception.JobHandle, exception.Attempt, cancellationToken))
				{
					throw exception.DatabaseException;
				}

				throw new ImmediateJobException(
					"The job operation could not be completed after repeated concurrency conflicts.",
					exception.DatabaseException
				);
			}
			catch (LostRaceException exception)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				throw new ImmediateJobException(
					"The job operation could not be completed after repeated concurrency conflicts.",
					exception
				);
			}
			catch
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				throw;
			}
		}
	}

	private async ValueTask<bool> SyntheticExecutionExistsAsync(
		JobHandle jobHandle,
		int attempt,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await using var scope = contextScope.GetScope(out var connection);

			return await Executions(connection)
				.AnyAsync(
					execution => execution.JobHandle == jobHandle.Value && execution.Attempt == attempt,
					cancellationToken
				);
		}
		catch (DbException)
		{
			return false;
		}
	}

	private static Task DelayConcurrencyRetryAsync(CancellationToken cancellationToken) =>
		Task.Delay(Random.Shared.Next(1, 6), cancellationToken);

	private async Task<bool> UpdateJobAsync(
		DataConnection connection,
		ImmediateJobEntity job,
		Guid oldStamp,
		CancellationToken cancellationToken
	)
	{
		var updated = await Jobs(connection)
			.Where(entity => entity.Id == job.Id && entity.ConcurrencyStamp == oldStamp)
			.Set(entity => entity.QueueName, job.QueueName)
			.Set(entity => entity.JobName, job.JobName)
			.Set(entity => entity.GroupId, job.GroupId)
			.Set(entity => entity.Payload, job.Payload)
			.Set(entity => entity.Context, job.Context)
			.Set(entity => entity.State, job.State)
			.Set(entity => entity.DueAt, job.DueAt)
			.Set(entity => entity.CreatedAt, job.CreatedAt)
			.Set(entity => entity.Attempt, job.Attempt)
			.Set(entity => entity.WorkerId, job.WorkerId)
			.Set(entity => entity.LeaseExpiresAt, job.LeaseExpiresAt)
			.Set(entity => entity.LastError, job.LastError)
			.Set(entity => entity.CompletedAt, job.CompletedAt)
			.Set(entity => entity.RecurringKey, job.RecurringKey)
			.Set(entity => entity.TraceParent, job.TraceParent)
			.Set(entity => entity.TraceState, job.TraceState)
			.Set(entity => entity.ExecutionTraceId, job.ExecutionTraceId)
			.Set(entity => entity.ExecutionSpanId, job.ExecutionSpanId)
			.Set(entity => entity.ExecutionStartedAt, job.ExecutionStartedAt)
			.Set(entity => entity.BatchHandle, job.BatchHandle)
			.Set(entity => entity.RemainingDependencies, job.RemainingDependencies)
			.Set(entity => entity.FailedDependencies, job.FailedDependencies)
			.Set(entity => entity.ConcurrencyStamp, job.ConcurrencyStamp)
			.UpdateAsync(cancellationToken);
		return updated != 0;
	}

	private async Task<bool> UpdateBatchAsync(
		DataConnection connection,
		ImmediateJobBatchEntity batch,
		Guid oldStamp,
		CancellationToken cancellationToken
	)
	{
		var updated = await Batches(connection)
			.Where(entity => entity.Id == batch.Id && entity.ConcurrencyStamp == oldStamp)
			.Set(entity => entity.CreatedAt, batch.CreatedAt)
			.Set(entity => entity.TotalJobs, batch.TotalJobs)
			.Set(entity => entity.PendingCount, batch.PendingCount)
			.Set(entity => entity.SucceededCount, batch.SucceededCount)
			.Set(entity => entity.FailedCount, batch.FailedCount)
			.Set(entity => entity.CancelledCount, batch.CancelledCount)
			.Set(entity => entity.SkippedCount, batch.SkippedCount)
			.Set(entity => entity.StartedAt, batch.StartedAt)
			.Set(entity => entity.CompletedAt, batch.CompletedAt)
			.Set(entity => entity.State, batch.State)
			.Set(entity => entity.ConcurrencyStamp, batch.ConcurrencyStamp)
			.UpdateAsync(cancellationToken);
		return updated != 0;
	}

#pragma warning disable CA1032, CA1064
	private sealed class LostRaceException : Exception;

	private sealed class SyntheticExecutionInsertFailedException(
		JobHandle jobHandle,
		int attempt,
		DbException databaseException
	) : Exception("A synthetic execution insert failed.", databaseException)
	{
		public JobHandle JobHandle { get; } = jobHandle;
		public int Attempt { get; } = attempt;
		public DbException DatabaseException { get; } = databaseException;
	}
}

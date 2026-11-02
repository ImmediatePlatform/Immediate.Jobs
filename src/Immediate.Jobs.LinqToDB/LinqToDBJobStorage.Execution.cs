using System.Data.Common;
using Immediate.Jobs.Shared.Apis;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	/// <inheritdoc />
	public async ValueTask SetExecutionTelemetryAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		SetExecutionTelemetryAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(async connection =>
		{
			// Fence ownership in the update itself. Most executions already have history, so
			// neither the job nor its execution needs to be read on the normal path.
			var updated = await Jobs(connection)
				.Where(item => item.Id == jobHandle.Value && item.Attempt == executionNumber && item.Attempt > 0
					&& item.State == JobState.Active && item.WorkerId == workerId)
				.Set(item => item.ExecutionTraceId, traceId)
				.Set(item => item.ExecutionSpanId, spanId)
				.Set(item => item.ExecutionStartedAt, startedAt)
				.Set(item => item.ConcurrencyStamp, Guid.NewGuid())
				.UpdateAsync(cancellationToken);
			if (updated == 0)
				throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
			var executionUpdated = await Executions(connection)
				.Where(execution => execution.JobHandle == jobHandle.Value && execution.Attempt == executionNumber && execution.State == JobExecutionState.Active)
				.Set(execution => execution.ExecutionTraceId, traceId)
				.Set(execution => execution.ExecutionSpanId, spanId)
				.Set(execution => execution.ExecutionStartedAt, startedAt)
				.UpdateAsync(cancellationToken);
			if (executionUpdated == 0)
			{
				var job = await Jobs(connection).SingleAsync(item => item.Id == jobHandle.Value, cancellationToken);
				// A persisted inactive execution is a conflict, not missing legacy history.
				if (await Executions(connection).AnyAsync(item => item.JobHandle == job.Id && item.Attempt == job.Attempt, cancellationToken))
					throw new LostRaceException();
				await MaterializeExecutionAsync(connection, job, cancellationToken);
			}
		}, cancellationToken);
	}

	private async Task PrepareAcquisitionExecutionsAsync(
		DataConnection connection,
		JobRecord previous,
		string workerId,
		DateTimeOffset acquiredAt,
		CancellationToken cancellationToken
	)
	{
		var priorExecution = await GetOrMaterializeExecutionAsync(connection, ToEntity(previous), cancellationToken);
		if (previous.State == JobState.Active && priorExecution is not null)
		{
			_ = await Executions(connection)
				.Where(execution => execution.JobHandle == previous.JobHandle.Value && execution.Attempt == previous.Attempt)
				.Set(execution => execution.State, JobExecutionState.Interrupted)
				.Set(execution => execution.CompletedAt, previous.LeaseExpiresAt)
				.Set(execution => execution.Error, (string?)null)
				.UpdateAsync(cancellationToken);
		}

		_ = await InsertAsync(connection, new ImmediateJobExecutionEntity
		{
			JobHandle = previous.JobHandle.Value,
			Attempt = previous.Attempt + 1,
			State = JobExecutionState.Active,
			WorkerId = workerId,
			AcquiredAt = acquiredAt,
		}, cancellationToken);
	}

	private async Task<ImmediateJobExecutionEntity?> GetOrMaterializeExecutionAsync(
		DataConnection connection,
		ImmediateJobEntity job,
		CancellationToken cancellationToken
	)
	{
		if (job.Attempt <= 0)
			return null;
		var execution = await Executions(connection)
			.SingleOrDefaultAsync(
				item => item.JobHandle == job.Id && item.Attempt == job.Attempt,
				cancellationToken
			);
		if (execution is not null)
			return execution;

		return await MaterializeExecutionAsync(connection, job, cancellationToken);
	}

	private async Task<ImmediateJobExecutionEntity?> MaterializeExecutionAsync(
		DataConnection connection,
		ImmediateJobEntity job,
		CancellationToken cancellationToken
	)
	{
		var synthetic = JobExecutionRecord.CreateSynthetic(ToRecord(job));
		if (synthetic is null)
			return null;
		var execution = ToEntity(synthetic);
		try
		{
			_ = await InsertAsync(connection, execution, cancellationToken);
		}
		catch (DbException exception)
		{
			throw new SyntheticExecutionInsertFailedException(JobHandle.FromString(job.Id), job.Attempt, exception);
		}

		return execution;
	}

	private async Task<ImmediateJobEntity> ReadOwnedJobAsync(
		DataConnection connection, JobHandle jobHandle, int executionNumber, string workerId,
		CancellationToken cancellationToken
	)
	{
		var owned = await Jobs(connection)
			.Where(job => job.Id == jobHandle.Value && job.Attempt == executionNumber
				&& job.State == JobState.Active && job.WorkerId == workerId)
			.Select(job => new
			{
				Job = job,
				HasExecution = Executions(connection).Any(execution => execution.JobHandle == job.Id && execution.Attempt == job.Attempt),
			})
			.SingleOrDefaultAsync(cancellationToken)
			?? throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
		if (owned.Job.Attempt <= 0)
			throw new ImmediateJobException($"Active job '{owned.Job.Id}' has no execution ordinal.");
		if (!owned.HasExecution)
			await MaterializeExecutionAsync(connection, owned.Job, cancellationToken);
		return owned.Job;
	}

	private async Task MaterializeExecutionsAsync(
		DataConnection connection, IEnumerable<ImmediateJobEntity> jobs, CancellationToken cancellationToken
	)
	{
		foreach (var block in jobs.Where(static job => job.Attempt > 0).Chunk(256))
		{
			var ids = block.Select(static job => job.Id).ToList();
			var latest = await Jobs(connection).Where(job => ids.Contains(job.Id))
				.Select(job => new
				{
					job.Id,
					job.Attempt,
					Exists = Executions(connection).Any(execution => execution.JobHandle == job.Id && execution.Attempt == job.Attempt),
				}).ToListAsync(cancellationToken);
			var existing = latest.Where(static job => job.Exists).Select(static job => (job.Id, job.Attempt)).ToHashSet();
			foreach (var job in block)
				if (!existing.Contains((job.Id, job.Attempt)))
					await MaterializeExecutionAsync(connection, job, cancellationToken);
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SetExecutionTelemetryAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.SetExecutionTelemetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SetExecutionTelemetryAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void SetExecutionTelemetryAsyncCalled(JobHandle jobHandle, int execution);
}

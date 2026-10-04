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
			var job = await Jobs(connection).SingleOrDefaultAsync(
				item => item.Id == jobHandle.Value && item.Attempt == executionNumber && item.State == JobState.Active && item.WorkerId == workerId,
				cancellationToken
			) ?? throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
			_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken)
				?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
			var oldStamp = job.ConcurrencyStamp;
			job.ExecutionTraceId = traceId;
			job.ExecutionSpanId = spanId;
			job.ExecutionStartedAt = startedAt;
			job.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
				throw new LostRaceException();
			var executionUpdated = await Executions(connection)
				.Where(execution => execution.JobHandle == jobHandle.Value && execution.Attempt == executionNumber && execution.State == JobExecutionState.Active)
				.Set(execution => execution.ExecutionTraceId, traceId)
				.Set(execution => execution.ExecutionSpanId, spanId)
				.Set(execution => execution.ExecutionStartedAt, startedAt)
				.UpdateAsync(cancellationToken);
			if (executionUpdated == 0)
				throw new LostRaceException();
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

		var synthetic = JobExecutionRecord.CreateSynthetic(ToRecord(job));
		if (synthetic is null)
			return null;
		execution = ToEntity(synthetic);
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

	[LoggerMessage(
		EventId = LibraryEventIds.SetExecutionTelemetryAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.SetExecutionTelemetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SetExecutionTelemetryAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void SetExecutionTelemetryAsyncCalled(JobHandle jobHandle, int execution);
}

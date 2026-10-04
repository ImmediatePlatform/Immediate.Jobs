using Immediate.Jobs.Shared.Apis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
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

		await MutateOwnedAsync(
			jobHandle,
			executionNumber,
			workerId,
			(job, execution) =>
			{
				job.ExecutionTraceId = traceId;
				job.ExecutionSpanId = spanId;
				job.ExecutionStartedAt = startedAt;
				execution.ExecutionTraceId = traceId;
				execution.ExecutionSpanId = spanId;
				execution.ExecutionStartedAt = startedAt;
			},
			cancellationToken
		);
	}

	private static async Task PrepareAcquisitionExecutionsAsync(
		TContext context,
		ImmediateJobEntity candidate,
		string workerId,
		DateTimeOffset acquiredAt,
		CancellationToken cancellationToken
	)
	{
		var previous = await GetOrMaterializeExecutionAsync(context, candidate, cancellationToken);
		if (candidate.State == JobState.Active && previous is not null)
		{
			previous.State = JobExecutionState.Interrupted;
			previous.CompletedAt = candidate.LeaseExpiresAt;
			previous.Error = null;
		}

		_ = context.Add(new ImmediateJobExecutionEntity
		{
			JobHandle = candidate.Id,
			Attempt = candidate.Attempt + 1,
			State = JobExecutionState.Active,
			WorkerId = workerId,
			AcquiredAt = acquiredAt,
		});
	}

	private static async Task<ImmediateJobExecutionEntity?> GetOrMaterializeExecutionAsync(
		TContext context,
		ImmediateJobEntity job,
		CancellationToken cancellationToken
	)
	{
		if (job.Attempt <= 0)
			return null;
		var execution = await context.Set<ImmediateJobExecutionEntity>()
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
		_ = context.Add(execution);
		return execution;
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SetExecutionTelemetryAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.SetExecutionTelemetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SetExecutionTelemetryAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void SetExecutionTelemetryAsyncCalled(JobHandle jobHandle, int execution);
}

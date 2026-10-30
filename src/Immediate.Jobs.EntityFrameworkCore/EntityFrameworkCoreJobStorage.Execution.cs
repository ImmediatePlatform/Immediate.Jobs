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
			.FindAsync([job.Id, job.Attempt], cancellationToken);
		if (execution is not null)
			return execution;

		var synthetic = JobExecutionRecord.CreateSynthetic(ToRecord(job));
		if (synthetic is null)
			return null;
		execution = ToEntity(synthetic);
		_ = context.Add(execution);
		return execution;
	}

	private static async Task MaterializeExecutionsAsync(
		TContext context,
		IEnumerable<ImmediateJobEntity> jobs,
		CancellationToken cancellationToken
	)
	{
		foreach (var batch in jobs.Where(static job => job.Attempt > 0).Chunk(1000))
		{
			var ids = batch.Select(static job => job.Id).ToList();
			var executions = await context.Set<ImmediateJobExecutionEntity>()
				.Where(execution => ids.Contains(execution.JobHandle) && context.Set<ImmediateJobEntity>()
					.Any(job => job.Id == execution.JobHandle && job.Attempt == execution.Attempt))
				.ToDictionaryAsync(static execution => (execution.JobHandle, execution.Attempt), cancellationToken);
			foreach (var job in batch)
			{
				if (!executions.ContainsKey((job.Id, job.Attempt)) && JobExecutionRecord.CreateSynthetic(ToRecord(job)) is { } synthetic)
					context.Add(ToEntity(synthetic));
			}
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SetExecutionTelemetryAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.SetExecutionTelemetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SetExecutionTelemetryAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void SetExecutionTelemetryAsyncCalled(JobHandle jobHandle, int execution);
}

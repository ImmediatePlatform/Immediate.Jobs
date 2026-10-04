using Immediate.Jobs.Shared.Apis;
using Microsoft.EntityFrameworkCore;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	private static ImmediateJobEntity ToEntity(JobRecord job) =>
		new()
		{
			Id = job.JobHandle.Value,
			QueueName = job.QueueName,
			JobName = job.JobName,
			GroupId = job.GroupId,
			Payload = job.Payload,
			Context = job.Context,
			State = job.State,
			DueAt = job.DueAt,
			CreatedAt = job.CreatedAt,
			Attempt = job.Attempt,
			WorkerId = job.WorkerId,
			LeaseExpiresAt = job.LeaseExpiresAt,
			LastError = job.LastError,
			CompletedAt = job.CompletedAt,
			RecurringKey = job.RecurringKey,
			TraceParent = job.TraceParent,
			TraceState = job.TraceState,
			ExecutionTraceId = job.ExecutionTraceId,
			ExecutionSpanId = job.ExecutionSpanId,
			ExecutionStartedAt = job.ExecutionStartedAt,
			BatchHandle = job.BatchHandle?.Value,
			RemainingDependencies = job.RemainingDependencies,
			FailedDependencies = job.FailedDependencies,
			ConcurrencyStamp = Guid.NewGuid(),
		};

	private static ImmediateJobExecutionEntity ToEntity(JobExecutionRecord execution) =>
		new()
		{
			JobHandle = execution.JobHandle.Value,
			Attempt = execution.Attempt,
			State = execution.State,
			WorkerId = execution.WorkerId,
			AcquiredAt = execution.AcquiredAt,
			ExecutionStartedAt = execution.ExecutionStartedAt,
			CompletedAt = execution.CompletedAt,
			ExecutionTraceId = execution.ExecutionTraceId,
			ExecutionSpanId = execution.ExecutionSpanId,
			Error = execution.Error,
			IsSynthetic = execution.IsSynthetic,
		};

	private static JobExecutionRecord ToRecord(ImmediateJobExecutionEntity execution) =>
		new()
		{
			JobHandle = JobHandle.FromString(execution.JobHandle),
			Attempt = execution.Attempt,
			State = execution.State,
			WorkerId = execution.WorkerId,
			AcquiredAt = execution.AcquiredAt,
			ExecutionStartedAt = execution.ExecutionStartedAt,
			CompletedAt = execution.CompletedAt,
			ExecutionTraceId = execution.ExecutionTraceId,
			ExecutionSpanId = execution.ExecutionSpanId,
			Error = execution.Error,
			IsSynthetic = execution.IsSynthetic,
		};

	private static ImmediateJobContinuationEntity ToEntity(JobContinuationEdge edge)
	{
		var (parentKind, parentId) = (edge.ParentJobHandle, edge.ParentBatchHandle) switch
		{
			({ Value: { } jobHandle }, null) => (ContinuationParentKind.Job, jobHandle),
			(null, { Value: { } batchHandle }) => (ContinuationParentKind.Batch, batchHandle),
			_ => throw new ImmediateJobException("A continuation edge must identify exactly one parent job or batch."),
		};

		return new()
		{
			ChildJobHandle = edge.ChildJobHandle.Value,
			ParentKind = parentKind,
			ParentId = parentId,
			Trigger = edge.Trigger,
			Delay = edge.Delay.Ticks,
		};
	}

	private static ImmediateJobEntity Copy(ImmediateJobEntity job) =>
		new()
		{
			Id = job.Id,
			QueueName = job.QueueName,
			JobName = job.JobName,
			GroupId = job.GroupId,
			Payload = job.Payload,
			Context = job.Context,
			State = job.State,
			DueAt = job.DueAt,
			CreatedAt = job.CreatedAt,
			Attempt = job.Attempt,
			WorkerId = job.WorkerId,
			LeaseExpiresAt = job.LeaseExpiresAt,
			LastError = job.LastError,
			CompletedAt = job.CompletedAt,
			RecurringKey = job.RecurringKey,
			TraceParent = job.TraceParent,
			TraceState = job.TraceState,
			ExecutionTraceId = job.ExecutionTraceId,
			ExecutionSpanId = job.ExecutionSpanId,
			ExecutionStartedAt = job.ExecutionStartedAt,
			BatchHandle = job.BatchHandle,
			RemainingDependencies = job.RemainingDependencies,
			FailedDependencies = job.FailedDependencies,
			ConcurrencyStamp = job.ConcurrencyStamp,
		};

	private static JobRecord ToRecord(ImmediateJobEntity job) =>
		new()
		{
			JobHandle = JobHandle.FromString(job.Id),
			QueueName = job.QueueName,
			JobName = job.JobName,
			GroupId = job.GroupId,
			Payload = job.Payload,
			Context = job.Context,
			State = job.State,
			DueAt = job.DueAt,
			CreatedAt = job.CreatedAt,
			Attempt = job.Attempt,
			WorkerId = job.WorkerId,
			LeaseExpiresAt = job.LeaseExpiresAt,
			LastError = job.LastError,
			CompletedAt = job.CompletedAt,
			RecurringKey = job.RecurringKey,
			TraceParent = job.TraceParent,
			TraceState = job.TraceState,
			ExecutionTraceId = job.ExecutionTraceId,
			ExecutionSpanId = job.ExecutionSpanId,
			ExecutionStartedAt = job.ExecutionStartedAt,
			BatchHandle = BatchHandle.FromString(job.BatchHandle),
			RemainingDependencies = job.RemainingDependencies,
			FailedDependencies = job.FailedDependencies,
		};

	private static BatchStatus ToStatus(ImmediateJobBatchEntity batch) =>
		new()
		{
			BatchHandle = BatchHandle.FromString(batch.Id),
			State = batch.State,
			Total = batch.TotalJobs,
			Succeeded = batch.SucceededCount,
			Failed = batch.FailedCount,
			Cancelled = batch.CancelledCount,
			Skipped = batch.SkippedCount,
			Remaining = batch.PendingCount,
			CreatedAt = batch.CreatedAt,
			StartedAt = batch.StartedAt,
			CompletedAt = batch.CompletedAt,
			FractionSettled = BatchStatus.CalculateFractionSettled(batch.TotalJobs, batch.PendingCount),
		};

	private static JobContinuationEdge ToContinuationEdge(ImmediateJobContinuationEntity edge) =>
		new()
		{
			ChildJobHandle = JobHandle.FromString(edge.ChildJobHandle),
			ParentJobHandle = edge.ParentKind == ContinuationParentKind.Job ? JobHandle.FromString(edge.ParentId) : null,
			ParentBatchHandle = edge.ParentKind == ContinuationParentKind.Batch ? BatchHandle.FromString(edge.ParentId) : null,
			Delay = TimeSpan.FromTicks(edge.Delay),
			Trigger = edge.Trigger,
		};

	private static ImmediateRecurringJobEntity ToEntity(RecurringJobSchedule schedule) =>
		new()
		{
			Name = schedule.Name,
			JobName = schedule.JobName,
			QueueName = schedule.QueueName,
			Cron = schedule.Cron,
			TimeZone = schedule.TimeZone,
			IsCodeDefined = schedule.IsCodeDefined,
			IsPaused = schedule.IsPaused,
			NextRunAt = schedule.NextRunAt,
			LastRunAt = schedule.LastRunAt,
			ConcurrencyStamp = Guid.NewGuid(),
		};
}

using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	private static IQueryable<JobQueryRow> JobsWithDefinitions(TContext context) =>
		from item in context.Set<ImmediateJobEntity>()
		join definition in context.Set<ImmediateJobDefinitionMetadataEntity>() on item.JobName equals definition.Name into definitions
		from definition in definitions.DefaultIfEmpty()
		select new JobQueryRow
		{
			CancellationReason = item.CancellationReason,
			Id = item.Id,
			JobName = item.JobName,
			GroupId = item.GroupId,
			Payload = item.Payload,
			Context = item.Context,
			State = item.State,
			DueAt = item.DueAt,
			CreatedAt = item.CreatedAt,
			Attempt = item.Attempt,
			WorkerId = item.WorkerId,
			LeaseExpiresAt = item.LeaseExpiresAt,
			LastError = item.LastError,
			CompletedAt = item.CompletedAt,
			RecurringKey = item.RecurringKey,
			TraceParent = item.TraceParent,
			TraceState = item.TraceState,
			ExecutionTraceId = item.ExecutionTraceId,
			ExecutionSpanId = item.ExecutionSpanId,
			ExecutionStartedAt = item.ExecutionStartedAt,
			BatchHandle = item.BatchHandle,
			RemainingDependencies = item.RemainingDependencies,
			FailedDependencies = item.FailedDependencies,
			ConcurrencyStamp = item.ConcurrencyStamp,
			QueueName = definition == null ? null : definition.QueueName,
		};

	[NotMapped]
	private sealed class JobQueryRow : ImmediateJobEntity
	{
		public string? QueueName { get; init; }
	}
}

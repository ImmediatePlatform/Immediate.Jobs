using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Immediate.Jobs.EntityFrameworkCore;

/// <summary>Adds the Immediate.Jobs persistence model to an application DbContext.</summary>
public static class ImmediateJobsModelBuilderExtensions
{
	/// <summary>Configures the entities required by the Immediate.Jobs EF Core storage provider.</summary>
	/// <param name="modelBuilder">The model builder to configure.</param>
	/// <param name="schema">The database schema for the Immediate.Jobs tables, or <see langword="null"/> for the provider default.</param>
	/// <returns>The configured model builder.</returns>
	public static ModelBuilder AddImmediateJobs(this ModelBuilder modelBuilder, string? schema = null)
	{
		ArgumentNullException.ThrowIfNull(modelBuilder);
		ConfigureBatches(modelBuilder.Entity<ImmediateJobBatchEntity>(), schema);
		ConfigureJobs(modelBuilder.Entity<ImmediateJobEntity>(), schema);
		ConfigureExecutions(modelBuilder.Entity<ImmediateJobExecutionEntity>(), schema);
		ConfigureFairQueueGroups(modelBuilder.Entity<ImmediateFairQueueGroupEntity>(), schema);
		ConfigureContinuations(modelBuilder.Entity<ImmediateJobContinuationEntity>(), schema);
		ConfigureRecurring(modelBuilder.Entity<ImmediateRecurringJobEntity>(), schema);
		ConfigureServers(modelBuilder.Entity<ImmediateJobServerEntity>(), schema);
		return modelBuilder;
	}

	private static void ConfigureExecutions(EntityTypeBuilder<ImmediateJobExecutionEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_job_executions", schema);
		_ = entity.HasKey(execution => new { execution.JobId, execution.Attempt });
		_ = entity.Property(execution => execution.JobId).HasMaxLength(256);
		_ = entity.Property(execution => execution.State).HasConversion<short>();
		_ = entity.Property(execution => execution.WorkerId).HasMaxLength(256);
		_ = entity.Property(execution => execution.AcquiredAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		_ = entity.Property(execution => execution.ExecutionStartedAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		_ = entity.Property(execution => execution.CompletedAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		_ = entity.Property(execution => execution.ExecutionTraceId).HasMaxLength(32);
		_ = entity.Property(execution => execution.ExecutionSpanId).HasMaxLength(16);
		_ = entity.Property(execution => execution.IsSynthetic).HasDefaultValue(value: false);
		_ = entity.HasOne<ImmediateJobEntity>()
			.WithMany()
			.HasForeignKey(execution => execution.JobId)
			.OnDelete(DeleteBehavior.Cascade);
	}

	private static void ConfigureBatches(EntityTypeBuilder<ImmediateJobBatchEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_job_batches", schema);
		_ = entity.HasKey(batch => batch.Id);
		_ = entity.Property(batch => batch.Id).HasMaxLength(256);
		_ = entity.Property(batch => batch.State).HasConversion<short>();
		_ = entity.Property(batch => batch.CreatedAt).HasConversion(
			value => value.UtcTicks,
			value => new DateTimeOffset(value, TimeSpan.Zero)
		);
		_ = entity.Property(batch => batch.StartedAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		_ = entity.Property(batch => batch.CompletedAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		_ = entity.Property(batch => batch.ConcurrencyStamp).IsConcurrencyToken();
		_ = entity.HasIndex(batch => new { batch.State, batch.CompletedAt });
	}

	private static void ConfigureJobs(EntityTypeBuilder<ImmediateJobEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_jobs", schema);
		_ = entity.HasKey(job => job.Id);
		_ = entity.Property(job => job.Id).HasMaxLength(256);
		_ = entity.Property(job => job.QueueName).HasMaxLength(256).HasDefaultValue(JobQueueDefinition.DefaultName).IsRequired();
		_ = entity.Property(job => job.JobName).HasMaxLength(256).IsRequired();
		_ = entity.Property(job => job.GroupId).HasMaxLength(128);
		_ = entity.Property(job => job.Payload).IsRequired();
		_ = entity.Property(job => job.Context).IsRequired(false);
		_ = entity.Property(job => job.State).HasConversion<short>();
		_ = entity.Property(job => job.DueAt).HasConversion(
			value => value.UtcTicks,
			value => new DateTimeOffset(value, TimeSpan.Zero)
		);
		_ = entity.Property(job => job.CreatedAt).HasConversion(
			value => value.UtcTicks,
			value => new DateTimeOffset(value, TimeSpan.Zero)
		);
		_ = entity.Property(job => job.LeaseExpiresAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		_ = entity.Property(job => job.CompletedAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		_ = entity.Property(job => job.ExecutionStartedAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		_ = entity.Property(job => job.WorkerId).HasMaxLength(256);
		_ = entity.Property(job => job.RecurringKey).HasMaxLength(512);
		_ = entity.Property(job => job.TraceParent).HasMaxLength(256);
		_ = entity.Property(job => job.ExecutionTraceId).HasMaxLength(32);
		_ = entity.Property(job => job.ExecutionSpanId).HasMaxLength(16);
		_ = entity.Property(job => job.BatchId).HasMaxLength(256);
		_ = entity.Property(job => job.ConcurrencyStamp).IsConcurrencyToken();
		_ = entity.HasOne<ImmediateJobBatchEntity>()
			.WithMany()
			.HasForeignKey(job => job.BatchId)
			.OnDelete(DeleteBehavior.Cascade);
		_ = entity.HasIndex(job => job.RecurringKey).IsUnique();
		_ = entity.HasIndex(job => job.BatchId);
		_ = entity.HasIndex(job => new { job.State, job.DueAt });
		_ = entity.HasIndex(job => new { job.State, job.CreatedAt });
		_ = entity.HasIndex(job => new { job.QueueName, job.State, job.DueAt, job.CreatedAt });
		_ = entity.HasIndex(job => new { job.QueueName, job.State, job.GroupId });
	}

	private static void ConfigureFairQueueGroups(
		EntityTypeBuilder<ImmediateFairQueueGroupEntity> entity,
		string? schema
	)
	{
		_ = entity.ToTable("immediate_fair_queue_groups", schema);
		_ = entity.HasKey(group => new { group.QueueName, group.GroupId });
		_ = entity.Property(group => group.QueueName).HasMaxLength(256);
		_ = entity.Property(group => group.GroupId).HasMaxLength(128);
		_ = entity.Property(group => group.ConcurrencyStamp).IsConcurrencyToken();
	}

	private static void ConfigureContinuations(
		EntityTypeBuilder<ImmediateJobContinuationEntity> entity,
		string? schema
	)
	{
		_ = entity.ToTable("immediate_job_continuations", schema);
		_ = entity.HasKey(edge => new { edge.ChildJobId, edge.ParentKind, edge.ParentId });
		_ = entity.Property(edge => edge.ChildJobId).HasMaxLength(256);
		_ = entity.Property(edge => edge.ParentKind).HasConversion<short>();
		_ = entity.Property(edge => edge.ParentId).HasMaxLength(256);
		_ = entity.Property(edge => edge.Delay).HasMaxLength(32);
		_ = entity.Property(edge => edge.Trigger).HasConversion<short>();
		_ = entity.Property(edge => edge.ParentOutcome).HasConversion<short>();
		_ = entity.HasOne<ImmediateJobEntity>()
			.WithMany()
			.HasForeignKey(edge => edge.ChildJobId)
			.OnDelete(DeleteBehavior.Cascade);
		_ = entity.HasIndex(edge => new { edge.ParentKind, edge.ParentId });
	}

	private static void ConfigureRecurring(EntityTypeBuilder<ImmediateRecurringJobEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_recurring_jobs", schema);
		_ = entity.HasKey(schedule => schedule.Name);
		_ = entity.Property(schedule => schedule.Name).HasMaxLength(256);
		_ = entity.Property(schedule => schedule.JobName).HasMaxLength(256).IsRequired();
		_ = entity.Property(schedule => schedule.QueueName).HasMaxLength(256).IsRequired();
		_ = entity.Property(schedule => schedule.Cron).HasMaxLength(128).IsRequired();
		_ = entity.Property(schedule => schedule.TimeZone).HasMaxLength(128).IsRequired();
		_ = entity.Property(schedule => schedule.NextRunAt).HasConversion(
			value => value.UtcTicks,
			value => new DateTimeOffset(value, TimeSpan.Zero)
		);
		_ = entity.Property(schedule => schedule.LastRunAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		_ = entity.Property(schedule => schedule.ConcurrencyStamp).IsConcurrencyToken();
		_ = entity.HasIndex(schedule => new { schedule.IsPaused, schedule.NextRunAt });
	}

	private static void ConfigureServers(EntityTypeBuilder<ImmediateJobServerEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_job_servers", schema);
		_ = entity.HasKey(server => server.WorkerId);
		_ = entity.Property(server => server.WorkerId).HasMaxLength(256);
		_ = entity.Property(server => server.LastHeartbeat).HasConversion(
			value => value.UtcTicks,
			value => new DateTimeOffset(value, TimeSpan.Zero)
		);
		_ = entity.HasIndex(server => server.LastHeartbeat);
	}
}

internal enum ContinuationParentKind : short
{
	Job,
	Batch,
}

internal enum ContinuationParentOutcome : short
{
	Unsettled,
	Succeeded,
	Failed,
	Other,
}

internal sealed class ImmediateJobBatchEntity
{
	public string Id { get; set; } = null!;
	public DateTimeOffset CreatedAt { get; set; }
	public int TotalJobs { get; set; }
	public int PendingCount { get; set; }
	public int SucceededCount { get; set; }
	public int FailedCount { get; set; }
	public int CancelledCount { get; set; }
	public int SkippedCount { get; set; }
	public DateTimeOffset? StartedAt { get; set; }
	public DateTimeOffset? CompletedAt { get; set; }
	public BatchState State { get; set; }
	public Guid ConcurrencyStamp { get; set; }
}

internal sealed class ImmediateJobEntity
{
	public string Id { get; set; } = null!;
	public string QueueName { get; set; } = JobQueueDefinition.DefaultName;
	public string JobName { get; set; } = null!;
	public string? GroupId { get; set; }
	public string Payload { get; set; } = null!;
	public string? Context { get; set; }
	public JobState State { get; set; }
	public DateTimeOffset DueAt { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public int Attempt { get; set; }
	public string? WorkerId { get; set; }
	public DateTimeOffset? LeaseExpiresAt { get; set; }
	public string? LastError { get; set; }
	public DateTimeOffset? CompletedAt { get; set; }
	public string? RecurringKey { get; set; }
	public string? TraceParent { get; set; }
	public string? TraceState { get; set; }
	public string? ExecutionTraceId { get; set; }
	public string? ExecutionSpanId { get; set; }
	public DateTimeOffset? ExecutionStartedAt { get; set; }
	public string? BatchId { get; set; }
	public int RemainingDependencies { get; set; }
	public int FailedDependencies { get; set; }
	public Guid ConcurrencyStamp { get; set; }
}

internal sealed class ImmediateJobExecutionEntity
{
	public string JobId { get; set; } = null!;
	public int Attempt { get; set; }
	public JobExecutionState State { get; set; }
	public string? WorkerId { get; set; }
	public DateTimeOffset? AcquiredAt { get; set; }
	public DateTimeOffset? ExecutionStartedAt { get; set; }
	public DateTimeOffset? CompletedAt { get; set; }
	public string? ExecutionTraceId { get; set; }
	public string? ExecutionSpanId { get; set; }
	public string? Error { get; set; }
	public bool IsSynthetic { get; set; }
}

internal sealed class ImmediateFairQueueGroupEntity
{
	public string QueueName { get; set; } = null!;
	public string GroupId { get; set; } = null!;
	public long LastServedSequence { get; set; }
	public Guid ConcurrencyStamp { get; set; }
}

internal sealed class ImmediateJobContinuationEntity
{
	public string ChildJobId { get; set; } = null!;
	public ContinuationParentKind ParentKind { get; set; }
	public string ParentId { get; set; } = null!;
	public string Delay { get; set; } = null!;
	public ContinuationTrigger Trigger { get; set; }
	public ContinuationParentOutcome ParentOutcome { get; set; }
}

internal sealed class ImmediateRecurringJobEntity
{
	public string Name { get; set; } = null!;
	public string JobName { get; set; } = null!;
	public string QueueName { get; set; } = null!;
	public string Cron { get; set; } = null!;
	public string TimeZone { get; set; } = null!;
	public bool IsCodeDefined { get; set; }
	public bool IsPaused { get; set; }
	public DateTimeOffset NextRunAt { get; set; }
	public DateTimeOffset? LastRunAt { get; set; }
	public Guid ConcurrencyStamp { get; set; }
}

internal sealed class ImmediateJobServerEntity
{
	public string WorkerId { get; set; } = null!;
	public DateTimeOffset LastHeartbeat { get; set; }
	public int ActiveWorkers { get; set; }
	public int MaxWorkers { get; set; }
}

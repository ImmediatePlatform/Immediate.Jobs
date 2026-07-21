using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Immediate.Jobs.EntityFrameworkCore;

/// <summary>Adds the Immediate.Jobs persistence model to an application DbContext.</summary>
public static class ImmediateJobsModelBuilderExtensions
{
	/// <summary>Configures the entities required by <see cref="EntityFrameworkCoreJobStorage{TContext}"/>.</summary>
	public static ModelBuilder AddImmediateJobs(this ModelBuilder modelBuilder, string? schema = null)
	{
		ArgumentNullException.ThrowIfNull(modelBuilder);
		ConfigureJobs(modelBuilder.Entity<ImmediateJobEntity>(), schema);
		ConfigureRecurring(modelBuilder.Entity<ImmediateRecurringJobEntity>(), schema);
		ConfigureServers(modelBuilder.Entity<ImmediateJobServerEntity>(), schema);
		return modelBuilder;
	}

	private static void ConfigureJobs(EntityTypeBuilder<ImmediateJobEntity> entity, string? schema)
	{
		entity.ToTable("immediate_jobs", schema);
		entity.HasKey(job => job.Id);
		entity.Property(job => job.QueueName).HasMaxLength(256).HasDefaultValue(JobQueueDefinition.DefaultName).IsRequired();
		entity.Property(job => job.JobName).HasMaxLength(256).IsRequired();
		entity.Property(job => job.Payload).IsRequired();
		entity.Property(job => job.Context).IsRequired(false);
		entity.Property(job => job.State).HasConversion<short>();
		entity.Property(job => job.DueAt).HasConversion(
			value => value.UtcTicks,
			value => new DateTimeOffset(value, TimeSpan.Zero)
		);
		entity.Property(job => job.CreatedAt).HasConversion(
			value => value.UtcTicks,
			value => new DateTimeOffset(value, TimeSpan.Zero)
		);
		entity.Property(job => job.LeaseExpiresAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		entity.Property(job => job.CompletedAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		entity.Property(job => job.WorkerId).HasMaxLength(256);
		entity.Property(job => job.RecurringKey).HasMaxLength(512);
		entity.Property(job => job.TraceParent).HasMaxLength(256);
		entity.Property(job => job.ConcurrencyStamp).IsConcurrencyToken();
		entity.HasIndex(job => job.RecurringKey).IsUnique();
		entity.HasIndex(job => new { job.State, job.DueAt });
		entity.HasIndex(job => new { job.State, job.CreatedAt });
		entity.HasIndex(job => new { job.QueueName, job.State, job.DueAt, job.CreatedAt });
	}

	private static void ConfigureRecurring(EntityTypeBuilder<ImmediateRecurringJobEntity> entity, string? schema)
	{
		entity.ToTable("immediate_recurring_jobs", schema);
		entity.HasKey(schedule => schedule.Name);
		entity.Property(schedule => schedule.Name).HasMaxLength(256);
		entity.Property(schedule => schedule.JobName).HasMaxLength(256).IsRequired();
		entity.Property(schedule => schedule.Cron).HasMaxLength(128).IsRequired();
		entity.Property(schedule => schedule.TimeZone).HasMaxLength(128).IsRequired();
		entity.Property(schedule => schedule.NextRunAt).HasConversion(
			value => value.UtcTicks,
			value => new DateTimeOffset(value, TimeSpan.Zero)
		);
		entity.Property(schedule => schedule.LastRunAt).HasConversion(
			value => value.HasValue ? value.Value.UtcTicks : (long?)null,
			value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
		);
		entity.Property(schedule => schedule.ConcurrencyStamp).IsConcurrencyToken();
		entity.HasIndex(schedule => new { schedule.IsPaused, schedule.NextRunAt });
	}

	private static void ConfigureServers(EntityTypeBuilder<ImmediateJobServerEntity> entity, string? schema)
	{
		entity.ToTable("immediate_job_servers", schema);
		entity.HasKey(server => server.WorkerId);
		entity.Property(server => server.WorkerId).HasMaxLength(256);
		entity.Property(server => server.LastHeartbeat).HasConversion(
			value => value.UtcTicks,
			value => new DateTimeOffset(value, TimeSpan.Zero)
		);
		entity.HasIndex(server => server.LastHeartbeat);
	}
}

internal sealed class ImmediateJobEntity
{
	public Guid Id { get; set; }
	public string QueueName { get; set; } = JobQueueDefinition.DefaultName;
	public string JobName { get; set; } = null!;
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
	public Guid ConcurrencyStamp { get; set; }
}

internal sealed class ImmediateRecurringJobEntity
{
	public string Name { get; set; } = null!;
	public string JobName { get; set; } = null!;
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

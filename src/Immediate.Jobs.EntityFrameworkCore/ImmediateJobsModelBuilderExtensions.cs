using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Immediate.Jobs.EntityFrameworkCore;

/// <summary>
/// 	Adds the Immediate.Jobs persistence model to an application DbContext.
/// </summary>
public static class ImmediateJobsModelBuilderExtensions
{
	private enum DatabaseEngine
	{
		SqlServer,
		PostgreSql,
		Sqlite,
	}

	/// <summary>
	/// 	Configures the entities required by the Immediate.Jobs EF Core storage provider.
	/// </summary>
	/// <param name="modelBuilder">
	/// 	The model builder to configure.
	/// </param>
	/// <param name="schema">
	/// 	The database schema for the Immediate.Jobs tables, or <see langword="null"/> for the provider default.
	/// </param>
	/// <param name="definitionNameCollation">
	/// 	An optional case-insensitive collation for job and recurring schedule names; defaults to the supported provider's case-insensitive collation.
	/// </param>
	/// <returns>
	/// 	The configured model builder.
	/// </returns>
	public static ModelBuilder AddImmediateJobs(this ModelBuilder modelBuilder, string? schema = null, string? definitionNameCollation = null)
	{
		ArgumentNullException.ThrowIfNull(modelBuilder);
		var databaseEngine = GetDatabaseEngine(modelBuilder);

		ConfigureDefinitions(modelBuilder.Entity<ImmediateJobDefinitionEntity>(), schema);
		ConfigureAcquisitions(modelBuilder.Entity<ImmediateJobAcquisitionEntity>(), schema);
		ConfigureBatches(modelBuilder.Entity<ImmediateJobBatchEntity>(), schema);
		ConfigureJobs(modelBuilder.Entity<ImmediateJobEntity>(), schema);
		ConfigureExecutions(modelBuilder.Entity<ImmediateJobExecutionEntity>(), schema);
		ConfigureFairQueueGroups(modelBuilder.Entity<ImmediateFairQueueGroupEntity>(), schema);
		ConfigureContinuations(modelBuilder.Entity<ImmediateJobContinuationEntity>(), schema);
		ConfigureRecurring(modelBuilder.Entity<ImmediateRecurringJobEntity>(), schema);
		ConfigureServers(modelBuilder.Entity<ImmediateJobServerEntity>(), schema);
		var definitions = modelBuilder.Entity<ImmediateJobDefinitionMetadataEntity>();
		definitions.ToTable("immediate_job_definition_metadata", schema);
		definitions.HasKey(definition => definition.Name);
		var postgresCollation = schema is null ? "immediate_jobs_case_insensitive" : schema + "_jobs_ci";
		definitionNameCollation ??= databaseEngine switch
		{
			DatabaseEngine.PostgreSql => postgresCollation,
			DatabaseEngine.SqlServer => null,
			DatabaseEngine.Sqlite => "NOCASE",
			_ => null,
		};
		if (databaseEngine == DatabaseEngine.PostgreSql && string.Equals(definitionNameCollation, postgresCollation, StringComparison.Ordinal))
		{
			// Npgsql's public collation metadata is stored as this annotation (EF Core 8, 10, and 11).
			modelBuilder.HasAnnotation("Npgsql:CollationDefinition:" + postgresCollation, "und-u-ks-level2,und-u-ks-level2,icu,False");
		}

		modelBuilder.Entity<ImmediateJobEntity>().Property(job => job.JobName).UseCollation(definitionNameCollation);
		modelBuilder.Entity<ImmediateRecurringJobEntity>().Property(schedule => schedule.JobName).UseCollation(definitionNameCollation);
		var recurringName = modelBuilder.Entity<ImmediateRecurringJobEntity>().Property(schedule => schedule.Name).UseCollation(definitionNameCollation);
		recurringName.Metadata.SetValueComparer(new ValueComparer<string>(
			(left, right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
			value => StringComparer.OrdinalIgnoreCase.GetHashCode(value),
			value => value));
		var name = definitions.Property(definition => definition.Name).HasMaxLength(256).UseCollation(definitionNameCollation);
		name.Metadata.SetValueComparer(new ValueComparer<string>(
			(left, right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
			value => StringComparer.OrdinalIgnoreCase.GetHashCode(value),
			value => value));
		definitions.Property(definition => definition.QueueName).HasMaxLength(256).IsRequired();
		definitions.Property(definition => definition.Cron).HasMaxLength(128);
		definitions.Property(definition => definition.TimeZone).HasMaxLength(128).IsRequired();
		definitions.Property(definition => definition.OverlapPolicy).HasConversion<short>();
		definitions.Property(definition => definition.MisfireHandlingMode).HasConversion<short>();
		definitions.Property(definition => definition.Backoff).HasConversion<short>();
		definitions.Property(definition => definition.Timeout).HasMaxLength(32);
		definitions.Property(definition => definition.BackoffBase).HasMaxLength(32);
		definitions.Property(definition => definition.SlidingWindowPeriod).HasMaxLength(32);
		definitions.Property(definition => definition.FixedWindowPeriod).HasMaxLength(32);
		var tags = definitions.Property(definition => definition.Tags).IsRequired();
		if (databaseEngine == DatabaseEngine.PostgreSql)
			tags.HasColumnType("jsonb");
		var catalogue = modelBuilder.Entity<ImmediateJobDefinitionCatalogEntity>();
		catalogue.ToTable("immediate_job_definition_catalog", schema);
		catalogue.HasKey(item => item.Id);
		catalogue.Property(item => item.Id).ValueGeneratedNever();
		ConfigureTimestamps(modelBuilder, databaseEngine);
		return modelBuilder;
	}

	private static DatabaseEngine GetDatabaseEngine(ModelBuilder modelBuilder)
	{
		if (modelBuilder.Model.FindAnnotation("Npgsql:ValueGenerationStrategy") is not null)
			return DatabaseEngine.PostgreSql;

		if (modelBuilder.Model.FindAnnotation("SqlServer:ValueGenerationStrategy") is not null)
			return DatabaseEngine.SqlServer;

		return DatabaseEngine.Sqlite;
	}

	private static void ConfigureTimestamps(ModelBuilder modelBuilder, DatabaseEngine databaseEngine)
	{
		if (databaseEngine == DatabaseEngine.SqlServer)
			return;

		ValueConverter converter = databaseEngine == DatabaseEngine.PostgreSql
			? new ValueConverter<DateTimeOffset, DateTimeOffset>(value => value.ToUniversalTime(), value => value)
			: new ValueConverter<DateTimeOffset, string>(
				value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
				value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture));

		foreach (var entity in modelBuilder.Model.GetEntityTypes())
		{
			if (!string.Equals(entity.ClrType.Namespace, typeof(ImmediateJobEntity).Namespace, StringComparison.Ordinal))
				continue;

			foreach (var property in entity.GetProperties())
			{
				if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTimeOffset))
					property.SetValueConverter(converter);
			}
		}
	}

	private static void ConfigureDefinitions(EntityTypeBuilder<ImmediateJobDefinitionEntity> entity, string? schema)
	{
		entity.ToTable("immediate_job_definitions", schema);
		entity.HasKey(item => item.JobName);
		entity.Property(item => item.JobName).HasMaxLength(256);
		entity.Property(item => item.AcquisitionStatus).HasConversion<short>();
		entity.Property(item => item.ConcurrencyStamp).IsConcurrencyToken();
	}

	private static void ConfigureAcquisitions(EntityTypeBuilder<ImmediateJobAcquisitionEntity> entity, string? schema)
	{
		entity.ToTable("immediate_job_acquisitions", schema);
		entity.HasKey(item => item.Id);
		entity.Property(item => item.JobName).HasMaxLength(256);
		entity.HasIndex(item => new { item.JobName, item.AcquiredAt });
	}

	private static void ConfigureExecutions(EntityTypeBuilder<ImmediateJobExecutionEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_job_executions", schema);
		_ = entity.HasKey(execution => new { execution.JobHandle, execution.Attempt });
		_ = entity.Property(execution => execution.JobHandle).HasMaxLength(256);
		_ = entity.Property(execution => execution.State).HasConversion<short>();
		_ = entity.Property(execution => execution.WorkerId).HasMaxLength(256);
		_ = entity.Property(execution => execution.ExecutionTraceId).HasMaxLength(32);
		_ = entity.Property(execution => execution.ExecutionSpanId).HasMaxLength(16);
		_ = entity.Property(execution => execution.IsSynthetic).HasDefaultValue(value: false);
		_ = entity.HasOne<ImmediateJobEntity>()
			.WithMany()
			.HasForeignKey(execution => execution.JobHandle)
			.OnDelete(DeleteBehavior.Cascade);
	}

	private static void ConfigureBatches(EntityTypeBuilder<ImmediateJobBatchEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_job_batches", schema);
		_ = entity.HasKey(batch => batch.Id);
		_ = entity.Property(batch => batch.Id).HasMaxLength(256);
		_ = entity.Property(batch => batch.State).HasConversion<short>();
		_ = entity.Property(batch => batch.ConcurrencyStamp).IsConcurrencyToken();
		_ = entity.HasIndex(batch => new { batch.State, batch.CompletedAt });
	}

	private static void ConfigureJobs(EntityTypeBuilder<ImmediateJobEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_jobs", schema);
		_ = entity.HasKey(job => job.Id);
		_ = entity.Property(job => job.Id).HasMaxLength(256);
		_ = entity.Property(job => job.JobName).HasMaxLength(256).IsRequired();
		_ = entity.Property(job => job.GroupId).HasMaxLength(128);
		_ = entity.Property(job => job.Payload).IsRequired();
		_ = entity.Property(job => job.Context).IsRequired(false);
		_ = entity.Property(job => job.State).HasConversion<short>();
		_ = entity.Property(job => job.WorkerId).HasMaxLength(256);
		_ = entity.Property(job => job.RecurringKey).HasMaxLength(512);
		_ = entity.Property(job => job.TraceParent).HasMaxLength(256);
		_ = entity.Property(job => job.ExecutionTraceId).HasMaxLength(32);
		_ = entity.Property(job => job.ExecutionSpanId).HasMaxLength(16);
		_ = entity.Property(job => job.BatchHandle).HasMaxLength(256);
		_ = entity.Property(job => job.ConcurrencyStamp).IsConcurrencyToken();
		_ = entity.HasOne<ImmediateJobBatchEntity>()
			.WithMany()
			.HasForeignKey(job => job.BatchHandle)
			.OnDelete(DeleteBehavior.Cascade);
		entity.HasIndex(job => new { job.JobName, job.State, job.LeaseExpiresAt });
		_ = entity.HasIndex(job => job.RecurringKey).IsUnique();
		_ = entity.HasIndex(job => job.BatchHandle);
		_ = entity.HasIndex(job => new { job.State, job.DueAt });
		_ = entity.HasIndex(job => new { job.State, job.CreatedAt });
		_ = entity.HasIndex(job => new { job.JobName, job.State, job.DueAt, job.CreatedAt });
		_ = entity.HasIndex(job => new { job.JobName, job.State, job.GroupId });
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
		_ = entity.HasKey(edge => new { edge.ChildJobHandle, edge.ParentKind, edge.ParentId });
		_ = entity.Property(edge => edge.ChildJobHandle).HasMaxLength(256);
		_ = entity.Property(edge => edge.ParentKind).HasConversion<short>();
		_ = entity.Property(edge => edge.ParentId).HasMaxLength(256);
		_ = entity.Property(edge => edge.Delay).HasMaxLength(32);
		_ = entity.Property(edge => edge.Trigger).HasConversion<short>();
		_ = entity.Property(edge => edge.ParentOutcome).HasConversion<short>();
		_ = entity.HasOne<ImmediateJobEntity>()
			.WithMany()
			.HasForeignKey(edge => edge.ChildJobHandle)
			.OnDelete(DeleteBehavior.Cascade);
		_ = entity.HasIndex(edge => new { edge.ParentKind, edge.ParentId });
	}

	private static void ConfigureRecurring(EntityTypeBuilder<ImmediateRecurringJobEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_recurring_jobs", schema);
		_ = entity.HasKey(schedule => schedule.Name);
		_ = entity.Property(schedule => schedule.Name).HasMaxLength(256);
		_ = entity.Property(schedule => schedule.JobName).HasMaxLength(256).IsRequired();
		_ = entity.Property(schedule => schedule.Cron).HasMaxLength(128).IsRequired();
		_ = entity.Property(schedule => schedule.TimeZone).HasMaxLength(128).IsRequired();
		_ = entity.Property(schedule => schedule.ConcurrencyStamp).IsConcurrencyToken();
		_ = entity.HasIndex(schedule => new { schedule.IsPaused, schedule.NextRunAt });
	}

	private static void ConfigureServers(EntityTypeBuilder<ImmediateJobServerEntity> entity, string? schema)
	{
		_ = entity.ToTable("immediate_job_servers", schema);
		_ = entity.HasKey(server => server.WorkerId);
		_ = entity.Property(server => server.WorkerId).HasMaxLength(256);
		_ = entity.Property(server => server.Details).IsRequired();
		_ = entity.HasIndex(server => server.ExpiresAt);
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

internal class ImmediateJobEntity
{
	public string? CancellationReason { get; set; }
	public string Id { get; set; } = null!;
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
	public string? BatchHandle { get; set; }
	public int RemainingDependencies { get; set; }
	public int FailedDependencies { get; set; }
	public Guid ConcurrencyStamp { get; set; }
}

internal sealed class ImmediateJobExecutionEntity
{
	public string JobHandle { get; set; } = null!;
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
	public string ChildJobHandle { get; set; } = null!;
	public ContinuationParentKind ParentKind { get; set; }
	public string ParentId { get; set; } = null!;
	public string Delay { get; set; } = null!;
	public ContinuationTrigger Trigger { get; set; }
	public ContinuationParentOutcome ParentOutcome { get; set; }
}

internal sealed class ImmediateRecurringJobEntity
{
	public string? CancellationReason { get; set; }
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
	public DateTimeOffset ExpiresAt { get; set; }
	public int ActiveWorkers { get; set; }
	public int MaxWorkers { get; set; }
	public string Details { get; set; } = null!;
}

internal sealed class ImmediateJobDefinitionMetadataEntity
{
	public string Name { get; set; } = null!;
	public string QueueName { get; set; } = null!;
	public int QueuePriority { get; set; }
	public int QueueConcurrency { get; set; }
	public string? Cron { get; set; }
	public string TimeZone { get; set; } = null!;
	public int MaxAttempts { get; set; }
	public string? Timeout { get; set; }
	public int MaxConcurrency { get; set; }
	public OverlapPolicy OverlapPolicy { get; set; }
	public MisfireHandlingMode MisfireHandlingMode { get; set; }
	public BackoffStrategy Backoff { get; set; }
	public string BackoffBase { get; set; } = null!;
	public int SlidingWindowMax { get; set; }
	public string? SlidingWindowPeriod { get; set; }
	public int FixedWindowMax { get; set; }
	public string? FixedWindowPeriod { get; set; }
	public string Tags { get; set; } = null!;
}

/// <summary>
/// 	A singleton row updated at the start of catalogue reconciliation to acquire a database write lock.
/// 	The lock serializes complete definition and recurring-schedule snapshots across servers until commit.
/// </summary>
internal sealed class ImmediateJobDefinitionCatalogEntity
{
	public int Id { get; set; }
	public Guid ConcurrencyStamp { get; set; }
}

internal sealed class ImmediateJobDefinitionEntity
{
	public string JobName { get; set; } = null!;
	public bool IsPaused { get; set; }
	public JobAcquisitionStatus AcquisitionStatus { get; set; }
	public DateTimeOffset? NextEligibleAt { get; set; }
	public DateTimeOffset? FixedWindowStart { get; set; }
	public int FixedWindowCount { get; set; }
	public Guid ConcurrencyStamp { get; set; }
}

internal sealed class ImmediateJobAcquisitionEntity
{
	public Guid Id { get; set; }
	public string JobName { get; set; } = null!;
	public DateTimeOffset AcquiredAt { get; set; }
}

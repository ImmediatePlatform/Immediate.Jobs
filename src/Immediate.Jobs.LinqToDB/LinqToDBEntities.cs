using LinqToDB;
using LinqToDB.Mapping;

namespace Immediate.Jobs.LinqToDB;

internal enum ContinuationParentKind : short
{
	Job,
	Batch,
}

internal enum ContinuationParentOutcome : short
{
	Unsettled = 0,
	Succeeded = 1,
	Failed = 2,
	Other = 3,
}

[Table(Name = "immediate_job_batches")]
internal sealed class ImmediateJobBatchEntity
{
	[PrimaryKey, Column(Length = 256, CanBeNull = false)]
	public string Id { get; set; } = null!;
	[Column(DataType = DataType.Int64)]
	public long CreatedAt { get; set; }
	[Column]
	public int TotalJobs { get; set; }
	[Column]
	public int PendingCount { get; set; }
	[Column]
	public int SucceededCount { get; set; }
	[Column]
	public int FailedCount { get; set; }
	[Column]
	public int CancelledCount { get; set; }
	[Column]
	public int SkippedCount { get; set; }
	[Column(DataType = DataType.Int64, CanBeNull = true)]
	public long? StartedAt { get; set; }
	[Column(DataType = DataType.Int64, CanBeNull = true)]
	public long? CompletedAt { get; set; }
	[Column(DataType = DataType.Int16)]
	public BatchState State { get; set; }
	[Column(DataType = DataType.Guid)]
	public Guid ConcurrencyStamp { get; set; }
}

[Table(Name = "immediate_jobs")]
internal sealed class ImmediateJobEntity
{
	[PrimaryKey, Column(Length = 256, CanBeNull = false)]
	public string Id { get; set; } = null!;
	[Column(Length = 256, CanBeNull = false)]
	public string QueueName { get; set; } = JobQueueDefinition.DefaultName;
	[Column(Length = 256, CanBeNull = false)]
	public string JobName { get; set; } = null!;
	[Column(Length = 128, CanBeNull = true)]
	public string? GroupId { get; set; }
	[Column(DataType = DataType.Text, CanBeNull = false)]
	public string Payload { get; set; } = null!;
	[Column(DataType = DataType.Text, CanBeNull = true)]
	public string? Context { get; set; }
	[Column(DataType = DataType.Int16)]
	public JobState State { get; set; }
	[Column(DataType = DataType.Int64)]
	public long DueAt { get; set; }
	[Column(DataType = DataType.Int64)]
	public long CreatedAt { get; set; }
	[Column]
	public int Attempt { get; set; }
	[Column(Length = 256, CanBeNull = true)]
	public string? WorkerId { get; set; }
	[Column(DataType = DataType.Int64, CanBeNull = true)]
	public long? LeaseExpiresAt { get; set; }
	[Column(DataType = DataType.Text, CanBeNull = true)]
	public string? LastError { get; set; }
	[Column(DataType = DataType.Int64, CanBeNull = true)]
	public long? CompletedAt { get; set; }
	[Column(Length = 512, CanBeNull = true)]
	public string? RecurringKey { get; set; }
	[Column(Length = 256, CanBeNull = true)]
	public string? TraceParent { get; set; }
	[Column(DataType = DataType.Text, CanBeNull = true)]
	public string? TraceState { get; set; }
	[Column(Length = 32, CanBeNull = true)]
	public string? ExecutionTraceId { get; set; }
	[Column(Length = 16, CanBeNull = true)]
	public string? ExecutionSpanId { get; set; }
	[Column(DataType = DataType.Int64, CanBeNull = true)]
	public long? ExecutionStartedAt { get; set; }
	[Column(Length = 256, CanBeNull = true)]
	public string? BatchId { get; set; }
	[Column]
	public int RemainingDependencies { get; set; }
	[Column]
	public int FailedDependencies { get; set; }
	[Column(DataType = DataType.Guid)]
	public Guid ConcurrencyStamp { get; set; }
}

[Table(Name = "immediate_job_executions")]
internal sealed class ImmediateJobExecutionEntity
{
	[PrimaryKey(1), Column(Length = 256, CanBeNull = false)]
	public string JobId { get; set; } = null!;
	[PrimaryKey(2), Column]
	public int Attempt { get; set; }
	[Column(DataType = DataType.Int16)]
	public JobExecutionState State { get; set; }
	[Column(Length = 256, CanBeNull = true)]
	public string? WorkerId { get; set; }
	[Column(DataType = DataType.Int64, CanBeNull = true)]
	public long? AcquiredAt { get; set; }
	[Column(DataType = DataType.Int64, CanBeNull = true)]
	public long? ExecutionStartedAt { get; set; }
	[Column(DataType = DataType.Int64, CanBeNull = true)]
	public long? CompletedAt { get; set; }
	[Column(Length = 32, CanBeNull = true)]
	public string? ExecutionTraceId { get; set; }
	[Column(Length = 16, CanBeNull = true)]
	public string? ExecutionSpanId { get; set; }
	[Column(DataType = DataType.Text, CanBeNull = true)]
	public string? Error { get; set; }
	[Column]
	public bool IsSynthetic { get; set; }
}

[Table(Name = "immediate_fair_queue_groups")]
internal sealed class ImmediateFairQueueGroupEntity
{
	[PrimaryKey(1), Column(Length = 256, CanBeNull = false)]
	public string QueueName { get; set; } = null!;
	[PrimaryKey(2), Column(Length = 128, CanBeNull = false)]
	public string GroupId { get; set; } = null!;
	[Column(DataType = DataType.Int64)]
	public long LastServedSequence { get; set; }
	[Column(DataType = DataType.Guid)]
	public Guid ConcurrencyStamp { get; set; }
}

[Table(Name = "immediate_job_continuations")]
internal sealed class ImmediateJobContinuationEntity
{
	[PrimaryKey(1), Column(Length = 256, CanBeNull = false)]
	public string ChildJobId { get; set; } = null!;
	[PrimaryKey(2), Column(DataType = DataType.Int16)]
	public ContinuationParentKind ParentKind { get; set; }
	[PrimaryKey(3), Column(Length = 256, CanBeNull = false)]
	public string ParentId { get; set; } = null!;
	[Column(DataType = DataType.Int16)]
	public ContinuationTrigger Trigger { get; set; }
	[Column(DataType = DataType.Int16)]
	public ContinuationParentOutcome ParentOutcome { get; set; }
}

[Table(Name = "immediate_recurring_jobs")]
internal sealed class ImmediateRecurringJobEntity
{
	[PrimaryKey, Column(Length = 256, CanBeNull = false)]
	public string Name { get; set; } = null!;
	[Column(Length = 256, CanBeNull = false)]
	public string JobName { get; set; } = null!;
	[Column(Length = 128, CanBeNull = false)]
	public string Cron { get; set; } = null!;
	[Column(Length = 128, CanBeNull = false)]
	public string TimeZone { get; set; } = null!;
	[Column]
	public bool IsCodeDefined { get; set; }
	[Column]
	public bool IsPaused { get; set; }
	[Column(DataType = DataType.Int64)]
	public long NextRunAt { get; set; }
	[Column(DataType = DataType.Int64, CanBeNull = true)]
	public long? LastRunAt { get; set; }
	[Column(DataType = DataType.Guid)]
	public Guid ConcurrencyStamp { get; set; }
}

[Table(Name = "immediate_job_servers")]
internal sealed class ImmediateJobServerEntity
{
	[PrimaryKey, Column(Length = 256, CanBeNull = false)]
	public string WorkerId { get; set; } = null!;
	[Column(DataType = DataType.Int64)]
	public long LastHeartbeat { get; set; }
	[Column]
	public int ActiveWorkers { get; set; }
	[Column]
	public int MaxWorkers { get; set; }
}

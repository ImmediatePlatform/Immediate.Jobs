using System.Diagnostics;

namespace Immediate.Jobs.Shared;

/// <summary>The durable lifecycle state of a job.</summary>
public enum JobState
{
	/// <summary>The job is delayed until its due time.</summary>
	Scheduled,
	/// <summary>The job is ready for acquisition.</summary>
	Pending,
	/// <summary>A worker owns the job lease.</summary>
	Active,
	/// <summary>The job completed successfully.</summary>
	Succeeded,
	/// <summary>The job exhausted all attempts.</summary>
	Failed,
	/// <summary>The job was cancelled.</summary>
	Cancelled,
}

/// <summary>A storage-neutral durable job record.</summary>
public sealed record JobRecord
{
	/// <summary>The stable persisted queue name.</summary>
	public string QueueName { get; init; } = JobQueueDefinition.DefaultName;

	/// <summary>The unique opaque invocation identifier.</summary>
	public required string Id { get; init; }

	/// <summary>The generated stable job name.</summary>
	public required string JobName { get; init; }

	/// <summary>The serialized payload.</summary>
	public required string Payload { get; init; }

	/// <summary>Serialized ambient-context envelope captured while enqueueing.</summary>
	public string? Context { get; init; }

	/// <summary>The current lifecycle state.</summary>
	public required JobState State { get; init; }

	/// <summary>UTC time at which the job may be acquired.</summary>
	public required DateTimeOffset DueAt { get; init; }

	/// <summary>UTC time at which the invocation was created.</summary>
	public required DateTimeOffset CreatedAt { get; init; }

	/// <summary>Number of attempts already started.</summary>
	public int Attempt { get; init; }

	/// <summary>The worker currently owning the record.</summary>
	public string? WorkerId { get; init; }

	/// <summary>UTC lease expiry for active work.</summary>
	public DateTimeOffset? LeaseExpiresAt { get; init; }

	/// <summary>The latest failure text.</summary>
	public string? LastError { get; init; }

	/// <summary>UTC terminal completion time.</summary>
	public DateTimeOffset? CompletedAt { get; init; }

	/// <summary>Unique recurring materialization key.</summary>
	public string? RecurringKey { get; init; }

	/// <summary>W3C trace parent captured while enqueueing.</summary>
	public string? TraceParent { get; init; }

	/// <summary>W3C trace state captured while enqueueing.</summary>
	public string? TraceState { get; init; }
}

/// <summary>A persisted recurring schedule.</summary>
public sealed record RecurringJobSchedule
{
	/// <summary>Unique schedule identity.</summary>
	public required string Name { get; init; }

	/// <summary>The generated job definition name.</summary>
	public required string JobName { get; init; }

	/// <summary>A five- or six-field cron expression.</summary>
	public required string Cron { get; init; }

	/// <summary>An IANA time-zone identifier.</summary>
	public required string TimeZone { get; init; }

	/// <summary>Whether this schedule originated in compiled code.</summary>
	public required bool IsCodeDefined { get; init; }

	/// <summary>Whether future scheduled occurrences are paused.</summary>
	public bool IsPaused { get; init; }

	/// <summary>The next scheduled occurrence in UTC.</summary>
	public required DateTimeOffset NextRunAt { get; init; }

	/// <summary>The most recently materialized scheduled occurrence in UTC.</summary>
	public DateTimeOffset? LastRunAt { get; init; }
}

/// <summary>Immutable generated execution settings.</summary>
public sealed record JobDefinition
{
	/// <summary>The queue used by newly-created invocations.</summary>
	public JobQueueDefinition Queue { get; init; } = JobQueueDefinition.Default;

	/// <summary>The stable job name.</summary>
	public required string Name { get; init; }

	/// <summary>The generated invoker.</summary>
	public required IJobInvoker Invoker { get; init; }

	/// <summary>The job CLR type, used only for logging and diagnostics.</summary>
	public required Type JobType { get; init; }

	/// <summary>The optional code-defined cron.</summary>
	public string? Cron { get; init; }

	/// <summary>The cron time-zone identifier.</summary>
	public string TimeZone { get; init; } = "UTC";

	/// <summary>Total allowed attempts.</summary>
	public int MaxAttempts { get; init; } = 3;

	/// <summary>Optional per-attempt timeout.</summary>
	public TimeSpan? Timeout { get; init; }

	/// <summary>Maximum executions of this job per node. Zero is unbounded.</summary>
	public int MaxConcurrency { get; init; }

	/// <summary>Recurring overlap behavior.</summary>
	public OverlapPolicy OverlapPolicy { get; init; } = OverlapPolicy.Skip;

	/// <summary>Retry-delay algorithm.</summary>
	public BackoffStrategy Backoff { get; init; } = BackoffStrategy.ExponentialJitter;

	/// <summary>Retry base delay.</summary>
	public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A monitoring query.</summary>
public sealed record JobQuery
{
	/// <summary>Optional exact invocation identifier.</summary>
	public string? Id { get; init; }

	/// <summary>Optional state filter.</summary>
	public JobState? State { get; init; }

	/// <summary>Optional exact queue name filter.</summary>
	public string? QueueName { get; init; }

	/// <summary>Optional case-insensitive job name search.</summary>
	public string? Search { get; init; }

	/// <summary>Number of records to skip.</summary>
	public int Skip { get; init; }

	/// <summary>Maximum records to return.</summary>
	public int Take { get; init; } = 100;
}

/// <summary>Immutable compile-time queue settings.</summary>
public sealed record JobQueueDefinition
{
	/// <summary>The built-in queue used by jobs without <c>UsesQueue</c>.</summary>
	public const string DefaultName = "default";

	/// <summary>The built-in default queue definition.</summary>
	public static JobQueueDefinition Default { get; } = new() { Name = DefaultName };

	/// <summary>The stable persisted queue name.</summary>
	public required string Name { get; init; }

	/// <summary>The dispatch priority. Larger values are dispatched first.</summary>
	public int Priority { get; init; }

	/// <summary>Maximum in-flight jobs on one scheduler node. Zero means unbounded.</summary>
	public int Concurrency { get; init; }
}

/// <summary>Describes the remaining acquisition capacity for one queue.</summary>
public sealed record JobQueueAcquisition
{
	/// <summary>The persisted queue name.</summary>
	public required string QueueName { get; init; }

	/// <summary>Maximum records to acquire from this queue.</summary>
	public required int Capacity { get; init; }

	/// <summary>Remaining acquisition capacity by stable job name.</summary>
	public required IReadOnlyDictionary<string, int> JobCapacities { get; init; }
}

/// <summary>A priority-ordered, node-local storage acquisition request.</summary>
public sealed record JobAcquisitionRequest
{
	/// <summary>The worker node taking ownership.</summary>
	public required string WorkerId { get; init; }

	/// <summary>The lease assigned to acquired records.</summary>
	public required TimeSpan Lease { get; init; }

	/// <summary>The maximum total records to acquire.</summary>
	public required int BatchSize { get; init; }

	/// <summary>Queues in dispatch order, with their remaining capacities.</summary>
	public required IReadOnlyList<JobQueueAcquisition> Queues { get; init; }
}

/// <summary>Queue totals for monitoring and health endpoints.</summary>
public sealed record JobMonitoringSnapshot(
	DateTimeOffset CapturedAt,
	IReadOnlyDictionary<JobState, long> Counts,
	IReadOnlyList<RecurringJobSchedule> Recurring,
	IReadOnlyList<JobServerSnapshot> Servers
);

/// <summary>A live scheduler-node heartbeat.</summary>
public sealed record JobServerSnapshot(
	string WorkerId,
	DateTimeOffset LastHeartbeat,
	int ActiveWorkers,
	int MaxWorkers
);

internal static class TraceContextCapture
{
	public static (string? Parent, string? State) Current()
	{
		var activity = Activity.Current;
		return (activity?.Id, activity?.TraceStateString);
	}
}

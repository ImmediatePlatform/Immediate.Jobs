using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared;

/// <summary>The durable lifecycle state of a job.</summary>
public enum JobState
{
	/// <summary>The job is parked until all continuation dependencies are satisfied.</summary>
	AwaitingContinuation,
	/// <summary>The job is parked until required inputs are supplied.</summary>
	/// <remarks>This state is unused at the moment, but reserved for future work.</remarks>
	AwaitingParameters,
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
	/// <summary>The job was not run because a continuation condition or scheduling policy was not met.</summary>
	Skipped,
}

/// <summary>A storage-neutral durable job record.</summary>
public sealed record JobRecord
{
	/// <summary>The stable persisted queue name.</summary>
	/// <value>The stable persisted queue name.</value>
	public string QueueName { get; init; } = JobQueueDefinition.DefaultName;

	/// <summary>The unique opaque invocation identifier.</summary>
	/// <value>The unique opaque invocation identifier.</value>
	public required string Id { get; init; }

	/// <summary>The generated stable job name.</summary>
	/// <value>The generated stable job name.</value>
	public required string JobName { get; init; }

	/// <summary>The serialized payload.</summary>
	/// <value>The serialized job payload.</value>
	[StringSyntax("json")]
	public required string Payload { get; init; }

	/// <summary>Serialized ambient-context envelope captured while enqueueing.</summary>
	/// <value>The serialized ambient-context envelope, if any.</value>
	[StringSyntax("json")]
	public string? Context { get; init; }

	/// <summary>Optional fairness group key scoped within the queue. Null means an independent tenant.</summary>
	/// <value>The queue-scoped fairness group key, or <see langword="null"/> for an independent tenant.</value>
	public string? GroupId { get; init; }

	/// <summary>The current lifecycle state.</summary>
	/// <value>The current job state.</value>
	public required JobState State { get; init; }

	/// <summary>UTC time at which the job may be acquired.</summary>
	/// <value>The UTC time at which the job may be acquired.</value>
	public required DateTimeOffset DueAt { get; init; }

	/// <summary>UTC time at which the invocation was created.</summary>
	/// <value>The UTC invocation-creation time.</value>
	public required DateTimeOffset CreatedAt { get; init; }

	/// <summary>Number of attempts already started.</summary>
	/// <value>The number of execution attempts already started.</value>
	public int Attempt { get; init; }

	/// <summary>The worker currently owning the record.</summary>
	/// <value>The owning worker identifier, or <see langword="null"/> when the job is not leased.</value>
	public string? WorkerId { get; init; }

	/// <summary>UTC lease expiry for active work.</summary>
	/// <value>The UTC lease-expiry time, or <see langword="null"/> when the job is not leased.</value>
	public DateTimeOffset? LeaseExpiresAt { get; init; }

	/// <summary>The latest failure text.</summary>
	/// <value>The latest failure details, if any.</value>
	public string? LastError { get; init; }

	/// <summary>UTC terminal completion time.</summary>
	/// <value>The UTC terminal completion time, if any.</value>
	public DateTimeOffset? CompletedAt { get; init; }

	/// <summary>Unique recurring materialization key.</summary>
	/// <value>The unique recurring materialization key, if this is a recurring occurrence.</value>
	public string? RecurringKey { get; init; }

	/// <summary>W3C trace parent captured while enqueueing.</summary>
	/// <value>The captured W3C trace parent, if any.</value>
	public string? TraceParent { get; init; }

	/// <summary>W3C trace state captured while enqueueing.</summary>
	/// <value>The captured W3C trace state, if any.</value>
	public string? TraceState { get; init; }

	/// <summary>Trace identifier created for the latest execution attempt.</summary>
	/// <value>The latest execution trace identifier, if one was created.</value>
	public string? ExecutionTraceId { get; init; }

	/// <summary>Span identifier created for the latest execution attempt.</summary>
	/// <value>The latest execution span identifier, if one was created.</value>
	public string? ExecutionSpanId { get; init; }

	/// <summary>UTC start time of the latest execution attempt.</summary>
	/// <value>The UTC start time of the latest execution attempt, if any.</value>
	public DateTimeOffset? ExecutionStartedAt { get; init; }

	/// <summary>The atomic batch containing this invocation, if any.</summary>
	/// <value>The containing batch identifier, if any.</value>
	public string? BatchId { get; init; }

	/// <summary>Number of incoming continuation dependencies not yet satisfied.</summary>
	/// <value>The number of unsatisfied incoming dependencies.</value>
	public int RemainingDependencies { get; init; }

	/// <summary>Number of settled incoming continuation dependencies whose parent failed.</summary>
	/// <value>The number of settled incoming dependencies whose parent failed.</value>
	public int FailedDependencies { get; init; }
}

/// <summary>A persisted recurring schedule.</summary>
public sealed record RecurringJobSchedule
{
	/// <summary>Unique schedule identity.</summary>
	/// <value>The unique schedule name.</value>
	public required string Name { get; init; }

	/// <summary>The generated job definition name.</summary>
	/// <value>The generated job definition name.</value>
	public required string JobName { get; init; }

	/// <summary>A five- or six-field cron expression.</summary>
	/// <value>The recurring cron expression.</value>
	public required string Cron { get; init; }

	/// <summary>An IANA time-zone identifier.</summary>
	/// <value>The IANA time-zone identifier.</value>
	public required string TimeZone { get; init; }

	/// <summary>Whether this schedule originated in compiled code.</summary>
	/// <value><see langword="true"/> for a code-defined schedule; otherwise, <see langword="false"/>.</value>
	public required bool IsCodeDefined { get; init; }

	/// <summary>Whether future scheduled occurrences are paused.</summary>
	/// <value><see langword="true"/> when future occurrences are paused; otherwise, <see langword="false"/>.</value>
	public bool IsPaused { get; init; }

	/// <summary>The next scheduled occurrence in UTC.</summary>
	/// <value>The next scheduled occurrence in UTC.</value>
	public required DateTimeOffset NextRunAt { get; init; }

	/// <summary>The most recently materialized scheduled occurrence in UTC.</summary>
	/// <value>The latest materialized occurrence in UTC, if any.</value>
	public DateTimeOffset? LastRunAt { get; init; }
}

/// <summary>Immutable generated execution settings.</summary>
[SuppressMessage("Design", "MA0053", Justification = "Inherited by each job to ensure idempotency in registrations")]
public record JobDefinition
{
	/// <summary>The queue used by newly-created invocations.</summary>
	/// <value>The queue definition used by new invocations.</value>
	public JobQueueDefinition Queue { get; init; } = JobQueueDefinition.Default;

	/// <summary>The stable job name.</summary>
	/// <value>The stable generated job name.</value>
	public required string Name { get; init; }

	/// <summary>The generated invoker.</summary>
	/// <value>The generated job invoker.</value>
	public required IJobInvoker Invoker { get; init; }

	/// <summary>The job CLR type, used only for logging and diagnostics.</summary>
	/// <value>The job CLR type.</value>
	public required Type JobType { get; init; }

	/// <summary>The optional code-defined cron.</summary>
	/// <value>The code-defined cron expression, or <see langword="null"/> for a non-recurring job.</value>
	public string? Cron { get; init; }

	/// <summary>The cron time-zone identifier.</summary>
	/// <value>The cron time-zone identifier.</value>
	public string TimeZone { get; init; } = "UTC";

	/// <summary>Total allowed attempts.</summary>
	/// <value>The total number of permitted execution attempts.</value>
	public int MaxAttempts { get; init; } = 3;

	/// <summary>Optional per-attempt timeout.</summary>
	/// <value>The per-attempt timeout, or <see langword="null"/> for no timeout.</value>
	public TimeSpan? Timeout { get; init; }

	/// <summary>Maximum executions of this job per node. Zero is unbounded.</summary>
	/// <value>The maximum executions per node, or zero for no limit.</value>
	public int MaxConcurrency { get; init; }

	/// <summary>Recurring overlap behavior.</summary>
	/// <value>The recurring-overlap policy.</value>
	public OverlapPolicy OverlapPolicy { get; init; } = OverlapPolicy.Skip;

	/// <summary>Retry-delay algorithm.</summary>
	/// <value>The retry-delay algorithm.</value>
	public BackoffStrategy Backoff { get; init; } = BackoffStrategy.ExponentialJitter;

	/// <summary>Retry base delay.</summary>
	/// <value>The retry base delay.</value>
	public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A monitoring query.</summary>
public sealed record JobQuery
{
	/// <summary>Optional exact invocation identifier.</summary>
	/// <value>The exact invocation identifier to match, or <see langword="null"/> to match any identifier.</value>
	public string? Id { get; init; }

	/// <summary>Optional state filter.</summary>
	/// <value>The lifecycle state to match, or <see langword="null"/> to match every state.</value>
	public JobState? State { get; init; }

	/// <summary>Optional exact queue name filter.</summary>
	/// <value>The exact queue name to match, or <see langword="null"/> to match every queue.</value>
	public string? QueueName { get; init; }

	/// <summary>Optional exact, case-sensitive job name filter.</summary>
	/// <value>The exact job name to match, or <see langword="null"/> to match every job name.</value>
	public string? JobName { get; init; }

	/// <summary>Optional case-insensitive job name search.</summary>
	/// <value>The case-insensitive job-name search text, or <see langword="null"/> to disable searching.</value>
	public string? Search { get; init; }

	/// <summary>Number of records to skip.</summary>
	/// <value>The number of records to skip.</value>
	public int Skip { get; init; }

	/// <summary>Maximum records to return.</summary>
	/// <value>The maximum number of records to return.</value>
	public int Take { get; init; } = 100;
}

/// <summary>Immutable compile-time queue settings.</summary>
public sealed record JobQueueDefinition
{
	/// <summary>The built-in queue used by jobs without <c>UsesQueue</c>.</summary>
	public const string DefaultName = "default";

	/// <summary>The built-in default queue definition.</summary>
	/// <value>The default queue definition.</value>
	public static JobQueueDefinition Default { get; } = new() { Name = DefaultName };

	/// <summary>The stable persisted queue name.</summary>
	/// <value>The stable persisted queue name.</value>
	public required string Name { get; init; }

	/// <summary>The dispatch priority. Larger values are dispatched first.</summary>
	/// <value>The queue dispatch priority.</value>
	public int Priority { get; init; }

	/// <summary>Maximum in-flight jobs on one scheduler node. Zero means unbounded.</summary>
	/// <value>The maximum in-flight jobs per node, or zero for no limit.</value>
	public int Concurrency { get; init; }
}

/// <summary>Describes the remaining acquisition capacity for one queue.</summary>
public sealed record JobQueueAcquisition
{
	/// <summary>The persisted queue name.</summary>
	/// <value>The persisted queue name.</value>
	public required string QueueName { get; init; }

	/// <summary>Maximum records to acquire from this queue.</summary>
	/// <value>The maximum number of records to acquire from the queue.</value>
	public required int Capacity { get; init; }

	/// <summary>Remaining acquisition capacity by stable job name.</summary>
	/// <value>The remaining acquisition capacity keyed by stable job name.</value>
	public required IReadOnlyDictionary<string, int> JobCapacities { get; init; }
}

/// <summary>Immutable fair queue settings applied by storage during one acquisition request.</summary>
/// <param name="ConcurrencyShareThreshold">The in-flight share threshold used to identify noisy groups.</param>
/// <param name="MinInflightForNoisy">The minimum in-flight job count required for noisy-group detection.</param>
/// <param name="GroupRoundRobin">Whether due work is interleaved across groups.</param>
public sealed record FairQueuePolicy(
	double ConcurrencyShareThreshold,
	int MinInflightForNoisy,
	bool GroupRoundRobin
);

/// <summary>A priority-ordered, node-local storage acquisition request.</summary>
public sealed record JobAcquisitionRequest
{
	/// <summary>The worker node taking ownership.</summary>
	/// <value>The identifier of the worker node taking ownership.</value>
	public required string WorkerId { get; init; }

	/// <summary>The lease assigned to acquired records.</summary>
	/// <value>The lease duration assigned to acquired records.</value>
	public required TimeSpan Lease { get; init; }

	/// <summary>The maximum total records to acquire.</summary>
	/// <value>The maximum total number of records to acquire.</value>
	public required int BatchSize { get; init; }

	/// <summary>Queues in dispatch order, with their remaining capacities.</summary>
	/// <value>The queues in dispatch order with their remaining capacities.</value>
	public required IReadOnlyList<JobQueueAcquisition> Queues { get; init; }

	/// <summary>Fair queue policy for this acquisition, or <see langword="null"/> when fairness is disabled.</summary>
	/// <value>The fair queue policy, or <see langword="null"/> when fairness is disabled.</value>
	public FairQueuePolicy? FairQueues { get; init; }
}

/// <summary>Queue totals for monitoring and health endpoints.</summary>
/// <param name="CapturedAt">The UTC time at which the snapshot was captured.</param>
/// <param name="Counts">The total job count for each lifecycle state.</param>
/// <param name="Recurring">The recurring schedules in the snapshot.</param>
/// <param name="Servers">The scheduler-node heartbeats in the snapshot.</param>
public sealed record JobMonitoringSnapshot(
	DateTimeOffset CapturedAt,
	IReadOnlyDictionary<JobState, long> Counts,
	IReadOnlyList<RecurringJobSchedule> Recurring,
	IReadOnlyList<JobServerSnapshot> Servers
)
{
	/// <summary>Capabilities implemented by the active storage provider.</summary>
	/// <value>The capabilities implemented by the active storage provider.</value>
	public StorageCapabilities Capabilities { get; init; } = StorageCapabilities.Queue;
}

/// <summary>A live scheduler-node heartbeat.</summary>
/// <param name="WorkerId">The scheduler-node identifier.</param>
/// <param name="LastHeartbeat">The UTC time of the latest scheduler heartbeat.</param>
/// <param name="ActiveWorkers">The number of active workers on the node.</param>
/// <param name="MaxWorkers">The maximum number of workers on the node.</param>
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

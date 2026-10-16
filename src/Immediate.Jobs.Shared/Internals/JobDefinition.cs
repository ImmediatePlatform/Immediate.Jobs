using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Immutable job configuration shared by execution, storage, and monitoring.
/// </summary>
[SuppressMessage("Design", "MA0053", Justification = "Inherited by each job to ensure idempotency in registrations")]
[EditorBrowsable(EditorBrowsableState.Never)]
public record JobDefinition
{
	/// <summary>
	/// 	The effective job tags. Omitted or empty lists use the default tag.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.AllowNull]
	public IReadOnlyList<string> Tags { get; init => field = JobTags.Normalize(value); } = JobTags.Normalize(tags: null);

	/// <summary>
	/// 	The queue used by newly-created invocations.
	/// </summary>
	[AllowNull]
	[JsonIgnore]
	public JobQueueDefinition Queue { get; init => field = value ?? JobQueueDefinition.Default; } = JobQueueDefinition.Default;

	/// <summary>
	/// 	The queue used for new invocations.
	/// </summary>
	public string QueueName { get => Queue.Name; init => Queue = Queue with { Name = value }; }

	/// <summary>
	/// 	The dispatch priority of the queue.
	/// </summary>
	public int QueuePriority { get => Queue.Priority; init => Queue = Queue with { Priority = value }; }

	/// <summary>
	/// 	The per-server queue concurrency limit. Zero is unbounded.
	/// </summary>
	public int QueueConcurrency { get => Queue.Concurrency; init => Queue = Queue with { Concurrency = value }; }

	/// <summary>
	/// 	The stable job name.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// 	The generated invoker, available only on locally registered definitions.
	/// </summary>
	[JsonIgnore]
	public IJobInvoker? Invoker { get; init; }

	/// <summary>
	/// 	The local job CLR type, used only for logging and diagnostics.
	/// </summary>
	[JsonIgnore]
	public Type? JobType { get; init; }

	/// <summary>
	/// 	The code-defined cron expression, or <see langword="null"/> for a non-recurring job.
	/// </summary>
	public string? Cron { get; init; }

	/// <summary>
	/// 	The cron time-zone identifier.
	/// </summary>
	public string TimeZone { get; init; } = "UTC";

	/// <summary>
	/// 	Total allowed attempts.
	/// </summary>
	public int MaxAttempts { get; init; } = 3;

	/// <summary>
	/// 	The per-attempt timeout, or <see langword="null"/> for no timeout.
	/// </summary>
	public TimeSpan? Timeout { get; init; }

	/// <summary>
	/// 	Maximum unexpired execution leases across workers. Zero is unbounded.
	/// </summary>
	public int MaxConcurrency { get; init; }

	/// <summary>
	/// 	Maximum acquisitions in the preceding sliding period. Zero disables this limit.
	/// </summary>
	public int SlidingWindowMax { get; init; }

	/// <summary>
	/// 	The sliding acquisition period, or null when disabled.
	/// </summary>
	public TimeSpan? SlidingWindowPeriod { get; init; }

	/// <summary>
	/// 	Maximum acquisitions in each aligned fixed period. Zero disables this limit.
	/// </summary>
	public int FixedWindowMax { get; init; }

	/// <summary>
	/// 	The fixed acquisition period, or null when disabled.
	/// </summary>
	public TimeSpan? FixedWindowPeriod { get; init; }

	/// <summary>
	/// 	The parsed limits passed to storage for direct acquisition queries.
	/// </summary>
	[JsonIgnore]
	public JobAcquisitionLimits AcquisitionLimits => new()
	{
		MaxConcurrency = MaxConcurrency,
		SlidingWindowMax = SlidingWindowMax,
		SlidingWindowPeriod = SlidingWindowPeriod,
		FixedWindowMax = FixedWindowMax,
		FixedWindowPeriod = FixedWindowPeriod,
	};

	/// <summary>
	/// 	Recurring overlap behavior.
	/// </summary>
	public OverlapPolicy OverlapPolicy { get; init; } = OverlapPolicy.Skip;

	/// <summary>
	/// 	Recurring misfire behavior.
	/// </summary>
	public MisfireHandlingMode MisfireHandlingMode { get; init; } = MisfireHandlingMode.EnqueueOne;

	/// <summary>
	/// 	Retry-delay algorithm.
	/// </summary>
	public BackoffStrategy Backoff { get; init; } = BackoffStrategy.ExponentialJitter;

	/// <summary>
	/// 	Retry base delay.
	/// </summary>
	public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(5);
}

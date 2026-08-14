using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Interfaces;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Immutable generated execution settings.
/// </summary>
[SuppressMessage("Design", "MA0053", Justification = "Inherited by each job to ensure idempotency in registrations")]
[EditorBrowsable(EditorBrowsableState.Never)]
public record JobDefinition
{
	/// <summary>
	/// 	The queue used by newly-created invocations.
	/// </summary>
	public JobQueueDefinition Queue { get; init; } = JobQueueDefinition.Default;

	/// <summary>
	/// 	The stable job name.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// 	The generated invoker.
	/// </summary>
	public required IJobInvoker Invoker { get; init; }

	/// <summary>
	/// 	The job CLR type, used only for logging and diagnostics.
	/// </summary>
	public required Type JobType { get; init; }

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
	/// 	Maximum executions of this job per node. Zero is unbounded.
	/// </summary>
	public int MaxConcurrency { get; init; }

	/// <summary>
	/// 	Recurring overlap behavior.
	/// </summary>
	public OverlapPolicy OverlapPolicy { get; init; } = OverlapPolicy.Skip;

	/// <summary>
	/// 	Retry-delay algorithm.
	/// </summary>
	public BackoffStrategy Backoff { get; init; } = BackoffStrategy.ExponentialJitter;

	/// <summary>
	/// 	Retry base delay.
	/// </summary>
	public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(5);
}

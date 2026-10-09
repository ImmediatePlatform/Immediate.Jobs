using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Marks a partial class as a generated background job.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class JobAttribute : Attribute
{
	/// <summary>
	/// 	The job routing tags. Omitted, null, or empty lists use the default tag.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.NotNull]
	public string[]? Tags { get; init => field = [.. JobTags.Normalize(value)]; } = [JobTags.Default];

	/// <summary>
	/// 	The explicit persisted job name, or <see langword="null"/> to derive it.
	/// </summary>
	/// <remarks>Names must not have leading or trailing whitespace.</remarks>
	/// <value>
	/// 	The explicit persisted job name, or <see langword="null"/> to derive it.
	/// </value>
	public string? Name { get; init; }

	/// <summary>
	/// 	A five- or six-field cron expression.
	/// </summary>
	/// <value>
	/// 	The cron expression, or <see langword="null"/> for a non-recurring job.
	/// </value>
	public string? Cron { get; init; }

	/// <summary>
	/// 	An IANA time-zone identifier. Defaults to UTC.
	/// </summary>
	/// <value>
	/// 	The IANA time-zone identifier, or <see langword="null"/> to use UTC.
	/// </value>
	public string? TimeZone { get; init; }

	/// <summary>
	/// 	Total execution attempts, including the first attempt.
	/// </summary>
	/// <value>
	/// 	The total number of permitted execution attempts.
	/// </value>
	public int MaxAttempts { get; init; } = 3;

	/// <summary>
	/// 	A <see cref="TimeSpan"/> formatted execution timeout.
	/// </summary>
	/// <value>
	/// 	The formatted execution timeout, or <see langword="null"/> for no timeout.
	/// </value>
	public string? Timeout { get; init; }

	/// <summary>
	/// 	Maximum simultaneous execution leases across all workers for this definition. Zero means unbounded.
	/// </summary>
	/// <value>
	/// 	The maximum simultaneous executions across workers, or zero for no limit.
	/// </value>
	public int MaxConcurrency { get; init; }

	/// <summary>
	/// 	Maximum acquisitions in the preceding sliding period. Zero disables this limit.
	/// </summary>
	public int SlidingWindowMax { get; init; }
	/// <summary>
	/// 	A positive TimeSpan-formatted sliding period, paired with SlidingWindowMax.
	/// </summary>
	public string? SlidingWindowPeriod { get; init; }
	/// <summary>
	/// 	Maximum acquisitions in each UTC-aligned fixed period. Zero disables this limit.
	/// </summary>
	public int FixedWindowMax { get; init; }
	/// <summary>
	/// 	A positive TimeSpan-formatted fixed period, paired with FixedWindowMax.
	/// </summary>
	public string? FixedWindowPeriod { get; init; }

	/// <summary>
	/// 	Controls what happens when a recurring invocation overlaps.
	/// </summary>
	/// <value>
	/// 	The recurring-overlap policy.
	/// </value>
	public OverlapPolicy OverlapPolicy { get; init; } = OverlapPolicy.Skip;

	/// <summary>
	/// 	Controls how recurring occurrences missed while the scheduler was unavailable are handled.
	/// </summary>
	/// <value>
	/// 	The recurring-misfire policy.
	/// </value>
	public MisfireHandlingMode MisfireHandlingMode { get; init; } = MisfireHandlingMode.EnqueueOne;

	/// <summary>
	/// 	The retry-delay algorithm.
	/// </summary>
	/// <value>
	/// 	The retry-delay algorithm.
	/// </value>
	public BackoffStrategy Backoff { get; init; } = BackoffStrategy.ExponentialJitter;

	/// <summary>
	/// 	A <see cref="TimeSpan"/> formatted retry base delay.
	/// </summary>
	/// <value>
	/// 	The formatted retry base delay.
	/// </value>
	public string BackoffBase { get; init; } = "00:00:05";
}

/// <summary>
/// 	Controls how missed recurring occurrences are handled.
/// </summary>
public enum MisfireHandlingMode
{
	/// <summary>
	/// 	Create an invocation for every missed occurrence.
	/// </summary>
	EnqueueAll,

	/// <summary>
	/// 	Create one immediately due invocation for all missed occurrences.
	/// </summary>
	EnqueueOne,

	/// <summary>
	/// 	Advance the schedule without creating invocations for missed occurrences.
	/// </summary>
	EnqueueNone,
}

/// <summary>
/// 	Controls overlapping recurring executions.
/// </summary>
public enum OverlapPolicy
{
	/// <summary>
	/// 	Do not create a recurring job invocation while an earlier invocation of the same schedule is being processed.
	/// </summary>
	Skip,

	/// <summary>
	/// 	Create the scheduled occurrence and run it after the earlier invocation.
	/// </summary>
	/// <remarks>
	/// 	Requires that the underlying storage is <see cref="IJobGraphStorage"/>.
	/// </remarks>
	Queue,

	/// <summary>
	/// 	Allow overlapping invocations.
	/// </summary>
	Concurrent,
}

/// <summary>
/// 	Controls retry delay calculation.
/// </summary>
public enum BackoffStrategy
{
	/// <summary>
	/// 	Always use the base delay.
	/// </summary>
	Fixed,
	/// <summary>
	/// 	Double the delay after every failed attempt.
	/// </summary>
	Exponential,
	/// <summary>
	/// 	Use exponential delay with bounded random jitter.
	/// </summary>
	ExponentialJitter,
}

namespace Immediate.Jobs.Shared;

/// <summary>Marks a partial class as a generated background job.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class JobAttribute : Attribute
{
	/// <summary>The explicit persisted job name, or <see langword="null"/> to derive it.</summary>
	public string? Name { get; init; }

	/// <summary>A five- or six-field cron expression.</summary>
	public string? Cron { get; init; }

	/// <summary>An IANA time-zone identifier. Defaults to UTC.</summary>
	public string? TimeZone { get; init; }

	/// <summary>Total execution attempts, including the first attempt.</summary>
	public int MaxAttempts { get; init; } = 3;

	/// <summary>A <see cref="TimeSpan"/> formatted execution timeout.</summary>
	public string? Timeout { get; init; }

	/// <summary>Maximum simultaneous executions per node. Zero means unbounded.</summary>
	public int MaxConcurrency { get; init; }

	/// <summary>Controls what happens when a recurring invocation overlaps.</summary>
	public OverlapPolicy OverlapPolicy { get; init; } = OverlapPolicy.Skip;

	/// <summary>The retry-delay algorithm.</summary>
	public BackoffStrategy Backoff { get; init; } = BackoffStrategy.ExponentialJitter;

	/// <summary>A <see cref="TimeSpan"/> formatted retry base delay.</summary>
	public string BackoffBase { get; init; } = "00:00:05";
}

/// <summary>Controls overlapping recurring executions.</summary>
public enum OverlapPolicy
{
	/// <summary>Do not create a recurring job invocation while an earlier invocation of the same schedule is being processed.</summary>
	Skip,
	/// <summary>Create the scheduled occurrence and run it after the earlier invocation.</summary>
	Queue,
	/// <summary>Allow overlapping invocations.</summary>
	Concurrent,
}

/// <summary>Controls retry delay calculation.</summary>
public enum BackoffStrategy
{
	/// <summary>Always use the base delay.</summary>
	Fixed,
	/// <summary>Double the delay after every failed attempt.</summary>
	Exponential,
	/// <summary>Use exponential delay with bounded random jitter.</summary>
	ExponentialJitter,
}

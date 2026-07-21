namespace Immediate.Jobs;

/// <summary>Marks a partial class as a generated background job.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class JobAttribute : Attribute
{
	/// <summary>Creates a job whose persisted name is derived from its class name.</summary>
	public JobAttribute()
	{
	}

	/// <summary>Creates a job with a stable persisted name.</summary>
	public JobAttribute(string name)
	{
		Name = name;
	}

	/// <summary>The explicit persisted job name, or <see langword="null"/> to derive it.</summary>
	public string? Name { get; }

	/// <summary>A five- or six-field cron expression.</summary>
	public string? Cron { get; set; }

	/// <summary>An IANA time-zone identifier. Defaults to UTC.</summary>
	public string? TimeZone { get; set; }

	/// <summary>Total execution attempts, including the first attempt.</summary>
	public int MaxAttempts { get; set; } = 3;

	/// <summary>A <see cref="TimeSpan"/> formatted execution timeout.</summary>
	public string? Timeout { get; set; }

	/// <summary>Maximum simultaneous executions per node. Zero means unbounded.</summary>
	public int MaxConcurrency { get; set; }

	/// <summary>Controls what happens when a recurring invocation overlaps.</summary>
	public OverlapPolicy OverlapPolicy { get; set; } = OverlapPolicy.Skip;

	/// <summary>The retry-delay algorithm.</summary>
	public BackoffStrategy Backoff { get; set; } = BackoffStrategy.ExponentialJitter;

	/// <summary>A <see cref="TimeSpan"/> formatted retry base delay.</summary>
	public string BackoffBase { get; set; } = "00:00:05";

	/// <summary>Behaviors applied only to this job, replacing assembly behaviors.</summary>
	public Type[]? Behaviors { get; set; }
}

/// <summary>Declares the ordered, assembly-wide job behavior pipeline.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class JobBehaviorsAttribute(params Type[] behaviorTypes) : Attribute
{
	/// <summary>The behavior types in outermost-to-innermost order.</summary>
	public IReadOnlyList<Type> BehaviorTypes { get; } = behaviorTypes;
}

/// <summary>Controls overlapping recurring executions.</summary>
public enum OverlapPolicy
{
	/// <summary>Do not materialize a tick while an earlier tick is active.</summary>
	Skip,
	/// <summary>Materialize the tick and run it after the earlier invocation.</summary>
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

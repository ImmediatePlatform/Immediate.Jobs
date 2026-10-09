namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Persisted, storage-neutral configuration for a job definition.
/// </summary>
public sealed record JobDefinitionRecord
{
	/// <summary>
	/// 	The effective job tags. Omitted or empty lists use the default tag.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.AllowNull]
	public IReadOnlyList<string> Tags { get; init => field = JobTags.Normalize(value); } = JobTags.Normalize(tags: null);

	/// <summary>
	/// 	The stable job name.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// 	The queue used for new invocations.
	/// </summary>
	public string QueueName { get; init; } = "default";

	/// <summary>
	/// 	The dispatch priority of the queue.
	/// </summary>
	public int QueuePriority { get; init; }

	/// <summary>
	/// 	The per-server queue concurrency limit. Zero is unbounded.
	/// </summary>
	public int QueueConcurrency { get; init; }

	/// <summary>
	/// 	The code-defined recurring expression, or null.
	/// </summary>
	public string? Cron { get; init; }

	/// <summary>
	/// 	The recurring time-zone identifier.
	/// </summary>
	public string TimeZone { get; init; } = "UTC";

	/// <summary>
	/// 	The total permitted execution attempts.
	/// </summary>
	public int MaxAttempts { get; init; } = 3;

	/// <summary>
	/// 	The execution timeout, or null.
	/// </summary>
	public TimeSpan? Timeout { get; init; }

	/// <summary>
	/// 	The per-server definition concurrency limit. Zero is unbounded.
	/// </summary>
	public int MaxConcurrency { get; init; }

	/// <summary>
	/// 	The recurring overlap policy.
	/// </summary>
	public OverlapPolicy OverlapPolicy { get; init; } = OverlapPolicy.Skip;

	/// <summary>
	/// 	The recurring misfire policy.
	/// </summary>
	public MisfireHandlingMode MisfireHandlingMode { get; init; } = MisfireHandlingMode.EnqueueOne;

	/// <summary>
	/// 	The retry delay algorithm.
	/// </summary>
	public BackoffStrategy Backoff { get; init; } = BackoffStrategy.ExponentialJitter;

	/// <summary>
	/// 	The retry base delay.
	/// </summary>
	public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(5);
}

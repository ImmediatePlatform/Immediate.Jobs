namespace Immediate.Jobs.Shared.Apis;

/// <summary>The current health of a scheduler maintenance loop.</summary>
public sealed record JobLoopSnapshot
{
	/// <summary>Whether an iteration is currently running.</summary>
	public bool IsRunning { get; init; }
	/// <summary>When the most recent iteration started.</summary>
	public DateTimeOffset? LastAttemptedAt { get; init; }
	/// <summary>When the most recent successful iteration completed.</summary>
	public DateTimeOffset? LastSucceededAt { get; init; }
	/// <summary>When the most recent failed iteration completed.</summary>
	public DateTimeOffset? LastFailedAt { get; init; }
	/// <summary>The number of consecutive failed iterations.</summary>
	public int ConsecutiveFailures { get; init; }
	/// <summary>The number of items considered by the most recent iteration.</summary>
	public int ItemsExamined { get; init; }
	/// <summary>The number of items successfully processed by the most recent iteration.</summary>
	public int ItemsSucceeded { get; init; }
	/// <summary>The number of items that failed during the most recent iteration.</summary>
	public int ItemsFailed { get; init; }
}

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	The current state of one worker in a scheduler node.
/// </summary>
public sealed record JobWorkerSnapshot
{
	/// <summary>The zero-based worker identifier within the scheduler node.</summary>
	public required int WorkerId { get; init; }

	/// <summary>The job currently being executed, or <see langword="null"/> when idle.</summary>
	public JobHandle? JobHandle { get; init; }

	/// <summary>The current execution attempt, or <see langword="null"/> when idle.</summary>
	public int? Attempt { get; init; }

	/// <summary>The time execution started, or <see langword="null"/> when idle.</summary>
	public DateTimeOffset? StartedAt { get; init; }
}

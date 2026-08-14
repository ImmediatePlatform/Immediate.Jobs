namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Monitoring data for one batch member.
/// </summary>
public sealed record BatchMemberStatus
{
	/// <summary>
	/// 	The invocation identifier.
	/// </summary>
	public required JobHandle JobId { get; init; }

	/// <summary>
	/// 	The stable job name.
	/// </summary>
	public required string JobName { get; init; }

	/// <summary>
	/// 	The persisted queue name.
	/// </summary>
	public required string QueueName { get; init; }

	/// <summary>
	/// 	The current invocation state.
	/// </summary>
	public required JobState State { get; init; }

	/// <summary>
	/// 	The number of execution attempts already started.
	/// </summary>
	public required int Attempt { get; init; }

	/// <summary>
	/// 	The UTC invocation-creation time.
	/// </summary>
	public required DateTimeOffset CreatedAt { get; init; }

	/// <summary>
	/// 	The UTC terminal completion time, if any.
	/// </summary>
	public DateTimeOffset? CompletedAt { get; init; }

	/// <summary>
	/// 	The latest failure details, if any.
	/// </summary>
	public string? LastError { get; init; }
}

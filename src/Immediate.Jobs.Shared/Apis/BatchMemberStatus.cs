namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Monitoring data for one batch member.
/// </summary>
/// <param name="JobId">
/// 	The invocation identifier.
/// </param>
/// <param name="JobName">
/// 	The stable job name.
/// </param>
/// <param name="QueueName">
/// 	The persisted queue name.
/// </param>
/// <param name="State">
/// 	The current invocation state.
/// </param>
/// <param name="Attempt">
/// 	The number of execution attempts already started.
/// </param>
/// <param name="CreatedAt">
/// 	The UTC invocation-creation time.
/// </param>
/// <param name="CompletedAt">
/// 	The UTC terminal completion time, if any.
/// </param>
/// <param name="LastError">
/// 	The latest failure details, if any.
/// </param>
public sealed record BatchMemberStatus(
	string JobId,
	string JobName,
	string QueueName,
	JobState State,
	int Attempt,
	DateTimeOffset CreatedAt,
	DateTimeOffset? CompletedAt,
	string? LastError
);

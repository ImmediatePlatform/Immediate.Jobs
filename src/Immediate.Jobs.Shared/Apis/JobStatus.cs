namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Monitoring data for one job.
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
/// <param name="MaxAttempts">
/// 	The total permitted execution attempts, if the job definition is available.
/// </param>
/// <param name="CreatedAt">
/// 	The UTC invocation-creation time.
/// </param>
/// <param name="DueAt">
/// 	The UTC time at which the invocation may be acquired.
/// </param>
/// <param name="CompletedAt">
/// 	The UTC terminal completion time, if any.
/// </param>
/// <param name="LastError">
/// 	The latest failure details, if any.
/// </param>
/// <param name="BatchId">
/// 	The containing batch identifier, if any.
/// </param>
/// <param name="DependsOn">
/// 	The invocation's incoming dependency edges.
/// </param>
public sealed record JobStatus(
	string JobId,
	string JobName,
	string QueueName,
	JobState State,
	int Attempt,
	int? MaxAttempts,
	DateTimeOffset CreatedAt,
	DateTimeOffset DueAt,
	DateTimeOffset? CompletedAt,
	string? LastError,
	string? BatchId,
	IReadOnlyList<BatchGraphEdge> DependsOn
);

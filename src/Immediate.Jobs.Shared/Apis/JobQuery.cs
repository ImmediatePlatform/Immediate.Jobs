using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A monitoring query.
/// </summary>
[Validate]
public sealed partial record JobQuery : IValidationTarget<JobQuery>
{
	/// <summary>
	/// 	The exact invocation identifier to match, or <see langword="null"/> to match any identifier.
	/// </summary>
	[NotEmpty]
	public string? JobId { get; init; }

	/// <summary>
	/// 	The lifecycle state to match, or <see langword="null"/> to match every state.
	/// </summary>
	public JobState? State { get; init; }

	/// <summary>
	/// 	The exact queue name to match, or <see langword="null"/> to match every queue.
	/// </summary>
	[NotEmpty]
	public string? QueueName { get; init; }

	/// <summary>
	/// 	The exact job name to match, or <see langword="null"/> to match every job name.
	/// </summary>
	[NotEmpty]
	public string? JobName { get; init; }

	/// <summary>
	/// 	The case-insensitive job-name search text, or <see langword="null"/> to disable searching.
	/// </summary>
	[NotEmpty]
	public string? Search { get; init; }

	/// <summary>
	/// 	The number of matching jobs to skip.
	/// </summary>
	[GreaterThanOrEqual(0)]
	public int Skip { get; init; }

	/// <summary>
	/// 	The maximum number of jobs to return.
	/// </summary>
	[GreaterThan(0)]
	[LessThanOrEqual(nameof(Constants.MaximumTake))]
	public int Take { get; init; } = 100;
}

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A monitoring query.
/// </summary>
public sealed record JobQuery
{
	/// <summary>
	/// 	The exact invocation identifier to match, or <see langword="null"/> to match any identifier.
	/// </summary>
	public string? Id { get; init; }

	/// <summary>
	/// 	The lifecycle state to match, or <see langword="null"/> to match every state.
	/// </summary>
	public JobState? State { get; init; }

	/// <summary>
	/// 	The exact queue name to match, or <see langword="null"/> to match every queue.
	/// </summary>
	public string? QueueName { get; init; }

	/// <summary>
	/// 	The exact job name to match, or <see langword="null"/> to match every job name.
	/// </summary>
	public string? JobName { get; init; }

	/// <summary>
	/// 	The case-insensitive job-name search text, or <see langword="null"/> to disable searching.
	/// </summary>
	public string? Search { get; init; }

	/// <summary>
	/// 	Number of records to skip.
	/// </summary>
	public int Skip { get; init; }

	/// <summary>
	/// 	Maximum records to return.
	/// </summary>
	public int Take { get; init; } = 100;
}

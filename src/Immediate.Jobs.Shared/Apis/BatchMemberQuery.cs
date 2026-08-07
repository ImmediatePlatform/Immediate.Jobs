namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Filters members returned from a batch.
/// </summary>
public sealed record BatchMemberQuery
{
	/// <summary>
	/// 	Optional lifecycle-state filter.
	/// </summary>
	/// <value>
	/// 	The lifecycle state to match, or <see langword="null"/> to match every state.
	/// </value>
	public JobState? State { get; init; }
	/// <summary>
	/// 	Number of members to skip.
	/// </summary>
	/// <value>
	/// 	The number of members to skip.
	/// </value>
	public int Skip { get; init; }
	/// <summary>
	/// 	Maximum members to return.
	/// </summary>
	/// <value>
	/// 	The maximum number of members to return.
	/// </value>
	public int Take { get; init; } = 100;
}

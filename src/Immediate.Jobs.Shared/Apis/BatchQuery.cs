namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Filters batches for dashboard presentation.
/// </summary>
public sealed record BatchQuery
{
	/// <summary>
	/// 	Optional aggregate-state filter.
	/// </summary>
	/// <value>
	/// 	The aggregate state to match, or <see langword="null"/> to match every state.
	/// </value>
	public BatchState? State { get; init; }
	/// <summary>
	/// 	Number of batches to skip.
	/// </summary>
	/// <value>
	/// 	The number of batches to skip.
	/// </value>
	public int Skip { get; init; }
	/// <summary>
	/// 	Maximum batches to return.
	/// </summary>
	/// <value>
	/// 	The maximum number of batches to return.
	/// </value>
	public int Take { get; init; } = 100;
}

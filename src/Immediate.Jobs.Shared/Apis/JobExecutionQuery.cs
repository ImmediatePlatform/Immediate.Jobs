namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Paging and exact-ordinal filters for retained job executions.
/// </summary>
public sealed record JobExecutionQuery
{
	/// <summary>
	/// 	The largest execution-history page returned by a storage provider.
	/// </summary>
	public const int MaximumTake = 1000;

	/// <summary>
	/// 	Validates the job identifier, exact ordinal, and paging values.
	/// </summary>
	public void Validate() => JobExecutionRecords.ValidateQuery(this);

	/// <summary>
	/// 	The owning job identifier.
	/// </summary>
	public required string JobId { get; init; }

	/// <summary>
	/// 	An exact execution ordinal, or <see langword="null"/> for newest-first history.
	/// </summary>
	public int? Attempt { get; init; }

	/// <summary>
	/// 	The number of matching executions to skip.
	/// </summary>
	public int Skip { get; init; }

	/// <summary>
	/// 	The maximum number of executions to return.
	/// </summary>
	public int Take { get; init; } = 100;
}

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Aggregate progress for an atomic batch.
/// </summary>
/// <param name="Id">
/// 	The opaque batch identifier.
/// </param>
/// <param name="State">
/// 	The aggregate batch state.
/// </param>
/// <param name="Total">
/// 	The total number of batch members.
/// </param>
/// <param name="Succeeded">
/// 	The number of successful members.
/// </param>
/// <param name="Failed">
/// 	The number of failed members.
/// </param>
/// <param name="Cancelled">
/// 	The number of cancelled members.
/// </param>
/// <param name="Skipped">
/// 	The number of skipped members.
/// </param>
/// <param name="Remaining">
/// 	The number of non-terminal members.
/// </param>
/// <param name="CreatedAt">
/// 	The UTC batch-creation time.
/// </param>
/// <param name="StartedAt">
/// 	The UTC time at which the first member was acquired, if any.
/// </param>
/// <param name="CompletedAt">
/// 	The UTC terminal completion time, if any.
/// </param>
/// <param name="FractionSettled">
/// 	The fraction of members that have reached a terminal state.
/// </param>
public sealed record BatchStatus(
	string Id,
	BatchState State,
	int Total,
	int Succeeded,
	int Failed,
	int Cancelled,
	int Skipped,
	int Remaining,
	DateTimeOffset CreatedAt,
	DateTimeOffset? StartedAt,
	DateTimeOffset? CompletedAt,
	double FractionSettled
)
{
	/// <summary>
	/// 	Calculates settled progress, treating an empty batch as fully settled.
	/// </summary>
	/// <param name="total">
	/// 	The total number of batch members.
	/// </param>
	/// <param name="remaining">
	/// 	The number of non-terminal members.
	/// </param>
	/// <returns>
	/// 	The fraction of members that have reached a terminal state.
	/// </returns>
	public static double CalculateFractionSettled(int total, int remaining) =>
		total == 0 ? 1d : (double)(total - remaining) / total;
}

/// <summary>
/// 	The durable lifecycle state of an atomic batch.
/// </summary>
public enum BatchState
{
	/// <summary>
	/// 	At least one member has not reached a terminal state.
	/// </summary>
	Executing,
	/// <summary>
	/// 	Every executed member succeeded; conditional branches may have been skipped.
	/// </summary>
	Succeeded,
	/// <summary>
	/// 	At least one member failed.
	/// </summary>
	Failed,
	/// <summary>
	/// 	No member failed and at least one member was cancelled.
	/// </summary>
	Cancelled,
}

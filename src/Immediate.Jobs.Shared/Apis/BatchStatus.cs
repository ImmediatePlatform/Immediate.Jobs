namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Aggregate progress for an atomic batch.
/// </summary>
public sealed record BatchStatus
{
	/// <summary>
	/// 	The opaque batch identifier.
	/// </summary>
	public required BatchHandle BatchHandle { get; init; }

	/// <summary>
	/// 	The aggregate batch state.
	/// </summary>
	public required BatchState State { get; init; }

	/// <summary>
	/// 	The total number of batch members.
	/// </summary>
	public required int Total { get; init; }

	/// <summary>
	/// 	The number of successful members.
	/// </summary>
	public required int Succeeded { get; init; }

	/// <summary>
	/// 	The number of failed members.
	/// </summary>
	public required int Failed { get; init; }

	/// <summary>
	/// 	The number of cancelled members.
	/// </summary>
	public required int Cancelled { get; init; }

	/// <summary>
	/// 	The number of skipped members.
	/// </summary>
	public required int Skipped { get; init; }

	/// <summary>
	/// 	The number of non-terminal members.
	/// </summary>
	public required int Remaining { get; init; }

	/// <summary>
	/// 	The UTC batch-creation time.
	/// </summary>
	public required DateTimeOffset CreatedAt { get; init; }

	/// <summary>
	/// 	The UTC time at which the first member was acquired, if any.
	/// </summary>
	public DateTimeOffset? StartedAt { get; init; }

	/// <summary>
	/// 	The UTC terminal completion time, if any.
	/// </summary>
	public DateTimeOffset? CompletedAt { get; init; }

	/// <summary>
	/// 	The fraction of members that have reached a terminal state.
	/// </summary>
	public required double FractionSettled { get; init; }

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

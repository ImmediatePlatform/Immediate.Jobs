namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A durable atomic-batch header.
/// </summary>
public sealed record BatchRecord
{
	/// <summary>
	/// 	The opaque batch identifier.
	/// </summary>
	/// <value>
	/// 	The opaque batch identifier.
	/// </value>
	public required BatchHandle BatchHandle { get; init; }

	/// <summary>
	/// 	UTC time at which the batch was created.
	/// </summary>
	/// <value>
	/// 	The UTC batch-creation time.
	/// </value>
	public required DateTimeOffset CreatedAt { get; init; }

	/// <summary>
	/// 	Total members added to the batch.
	/// </summary>
	/// <value>
	/// 	The total number of members added to the batch.
	/// </value>
	public required int TotalJobs { get; init; }

	/// <summary>
	/// 	Members that have not reached a terminal state.
	/// </summary>
	/// <value>
	/// 	The number of non-terminal members.
	/// </value>
	public required int PendingCount { get; init; }

	/// <summary>
	/// 	Members that completed successfully.
	/// </summary>
	/// <value>
	/// 	The number of successful members.
	/// </value>
	public int SucceededCount { get; init; }

	/// <summary>
	/// 	Members that exhausted their attempts.
	/// </summary>
	/// <value>
	/// 	The number of failed members.
	/// </value>
	public int FailedCount { get; init; }

	/// <summary>
	/// 	Members cancelled by an explicit action.
	/// </summary>
	/// <value>
	/// 	The number of cancelled members.
	/// </value>
	public int CancelledCount { get; init; }

	/// <summary>
	/// 	Members skipped because their continuation conditions were not met.
	/// </summary>
	/// <value>
	/// 	The number of skipped members.
	/// </value>
	public int SkippedCount { get; init; }

	/// <summary>
	/// 	UTC time at which the first member was acquired.
	/// </summary>
	/// <value>
	/// 	The UTC batch-start time, if any member has been acquired.
	/// </value>
	public DateTimeOffset? StartedAt { get; init; }

	/// <summary>
	/// 	UTC terminal completion time.
	/// </summary>
	/// <value>
	/// 	The UTC terminal completion time, if the batch is terminal.
	/// </value>
	public DateTimeOffset? CompletedAt { get; init; }

	/// <summary>
	/// 	The aggregate lifecycle state.
	/// </summary>
	/// <value>
	/// 	The aggregate batch state.
	/// </value>
	public required BatchState State { get; init; }
}

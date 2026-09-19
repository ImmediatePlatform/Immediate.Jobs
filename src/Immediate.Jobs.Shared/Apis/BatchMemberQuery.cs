using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Filters members returned from a batch.
/// </summary>
[Validate]
public sealed partial record BatchMemberQuery : IValidationTarget<BatchMemberQuery>
{
	/// <summary>
	/// 	The lifecycle state to match, or <see langword="null"/> to match every state.
	/// </summary>
	public JobState? State { get; init; }

	/// <summary>
	/// 	Number of members to skip.
	/// </summary>
	[GreaterThanOrEqual(0)]
	public int Skip { get; init; }

	/// <summary>
	/// 	Maximum members to return.
	/// </summary>
	[GreaterThan(0)]
	[LessThanOrEqual(nameof(Constants.MaximumTake))]
	public int Take { get; init; } = 100;
}

using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Filters batches for dashboard presentation.
/// </summary>
[Validate]
public sealed partial record BatchQuery : IValidationTarget<BatchQuery>
{
	/// <summary>
	/// 	Optional aggregate-state filter.
	/// </summary>
	public BatchState? State { get; init; }

	/// <summary>
	/// 	The number of matching batches to skip.
	/// </summary>
	[GreaterThanOrEqual(0)]
	public int Skip { get; init; }

	/// <summary>
	/// 	The maximum number of batches to return.
	/// </summary>
	[GreaterThan(0)]
	[LessThanOrEqual(nameof(Constants.MaximumTake))]
	public int Take { get; init; } = 100;
}

using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Paging and exact-ordinal filters for retained job executions.
/// </summary>
[Validate]
public sealed partial record JobExecutionQuery : IValidationTarget<JobExecutionQuery>
{
	/// <summary>
	/// 	The owning job identifier.
	/// </summary>
	public required string JobId { get; init; }

	/// <summary>
	/// 	An exact execution ordinal, or <see langword="null"/> for newest-first history.
	/// </summary>
	[GreaterThan(0)]
	public int? Attempt { get; init; }

	/// <summary>
	/// 	The number of matching executions to skip.
	/// </summary>
	[GreaterThanOrEqual(0)]
	public int Skip { get; init; }

	/// <summary>
	/// 	The maximum number of executions to return.
	/// </summary>
	[GreaterThan(0)]
	[LessThanOrEqual(nameof(Constants.MaximumTake))]
	public int Take { get; init; } = 100;
}

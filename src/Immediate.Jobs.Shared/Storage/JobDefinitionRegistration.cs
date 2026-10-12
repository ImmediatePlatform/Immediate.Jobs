using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	The complete application startup catalogue.
/// </summary>
public sealed record JobDefinitionRegistration
{
	/// <summary>
	/// 	All locally registered definitions, provided at startup.
	/// </summary>
	public required IReadOnlyList<JobDefinitionRecord> Definitions { get; init; }

	/// <summary>
	/// 	The complete list of code-defined recurring schedules for these definitions.
	/// </summary>
	public IReadOnlyList<RecurringJobSchedule> RecurringSchedules { get; init; } = [];
}

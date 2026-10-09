using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	The complete application startup catalogue and its management scope.
/// </summary>
public sealed record JobDefinitionRegistration
{
	/// <summary>
	/// 	The effective server tags. Omitted or empty lists use the default tag.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.AllowNull]
	public IReadOnlyList<string> ServerTags { get; init => field = JobTags.Normalize(value); } = JobTags.Normalize(tags: null);

	/// <summary>
	/// 	All locally registered definitions, including those outside the server's scope.
	/// </summary>
	public required IReadOnlyList<JobDefinitionRecord> Definitions { get; init; }

	/// <summary>
	/// 	The complete list of code-defined recurring schedules for these definitions.
	/// </summary>
	public IReadOnlyList<RecurringJobSchedule> RecurringSchedules { get; init; } = [];
}

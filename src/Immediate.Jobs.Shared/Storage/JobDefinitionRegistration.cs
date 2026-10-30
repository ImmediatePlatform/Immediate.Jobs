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
	public IReadOnlyList<string> ServerTags { get; init => field = JobTags.Normalize(value); } = JobTags.Normalize(tags: null);

	/// <summary>
	/// 	All locally registered definitions, including those outside the server's scope.
	/// </summary>
	public required IReadOnlyList<JobDefinitionRecord> Definitions { get; init; }

	/// <summary>
	/// 	The next occurrence of each code-defined recurring job, keyed by case-insensitive job name.
	/// </summary>
	public required IReadOnlyDictionary<string, DateTimeOffset> RecurringSchedules { get; init; }
}

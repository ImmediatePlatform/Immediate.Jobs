namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Stored job and recurring configuration, independent of live monitoring status.
/// </summary>
public sealed record JobMonitoringDefinitions
{
	/// <summary>
	/// 	All persisted job definitions, irrespective of server tags or local registrations.
	/// </summary>
	public required IReadOnlyList<JobDefinitionRecord> Jobs { get; init; }

	/// <summary>
	/// 	All code-defined and dynamic recurring schedule definitions.
	/// </summary>
	public required IReadOnlyList<RecurringJobDefinition> Recurring { get; init; }
}

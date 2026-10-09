using System.ComponentModel;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Storage-neutral changes computed inside a provider's atomic startup operation.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record JobDefinitionReconciliation
{
	/// <summary>
	/// 	The definitions to insert or replace.
	/// </summary>
	public required IReadOnlyList<JobDefinitionRecord> Definitions { get; init; }

	/// <summary>
	/// 	The names of obsolete definitions in this server's scope.
	/// </summary>
	public required IReadOnlyList<string> RemovedDefinitionNames { get; init; }

	/// <summary>
	/// 	The merged code-defined schedules, preserving pause and unchanged progress.
	/// </summary>
	public required IReadOnlyList<RecurringJobSchedule> Schedules { get; init; }

	/// <summary>
	/// 	The names of obsolete code-defined schedules in this server's scope.
	/// </summary>
	public required IReadOnlyList<string> RemovedScheduleNames { get; init; }

	/// <summary>
	/// 	Validates a complete catalogue and computes changes against the provider's locked snapshot.
	/// </summary>
	/// <param name="registration">
	/// 	The application's complete catalogue and normalized server tags.
	/// </param>
	/// <param name="existingDefinitions">
	/// 	The stored definitions before any tag changes.
	/// </param>
	/// <param name="existingSchedules">
	/// 	The stored recurring schedules before this reconciliation.
	/// </param>
	/// <returns>
	/// 	The scoped changes to commit atomically.
	/// </returns>
	public static JobDefinitionReconciliation Create(
		JobDefinitionRegistration registration,
		IReadOnlyList<JobDefinitionRecord> existingDefinitions,
		IReadOnlyList<RecurringJobSchedule> existingSchedules
	)
	{
		ArgumentNullException.ThrowIfNull(registration);
		ArgumentNullException.ThrowIfNull(existingDefinitions);
		ArgumentNullException.ThrowIfNull(existingSchedules);
		ArgumentNullException.ThrowIfNull(registration.Definitions, nameof(registration));
		ArgumentNullException.ThrowIfNull(registration.RecurringSchedules, nameof(registration));
		var supplied = registration.Definitions.ToDictionary(static definition => definition.Name, StringComparer.OrdinalIgnoreCase);
		foreach (var definition in supplied.Values)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name, nameof(registration));
			if (char.IsWhiteSpace(definition.Name[0]) || char.IsWhiteSpace(definition.Name[^1]))
				throw new ArgumentException("Job definition names must not have leading or trailing whitespace.", nameof(registration));
		}

		var canonicalNames = existingDefinitions.ToDictionary(static definition => definition.Name, static definition => definition.Name, StringComparer.OrdinalIgnoreCase);
		var desired = supplied.Values
			.Where(definition => JobTags.Intersect(definition.Tags, registration.ServerTags))
			.Select(definition => canonicalNames.TryGetValue(definition.Name, out var name) ? definition with { Name = name } : definition)
			.ToDictionary(static definition => definition.Name, StringComparer.OrdinalIgnoreCase);
		var managedNames = existingDefinitions
			.Where(definition => JobTags.Intersect(definition.Tags, registration.ServerTags))
			.Select(static definition => definition.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var removedDefinitions = managedNames.Where(name => !desired.ContainsKey(name)).ToList();
		// Schedules persisted before the definition catalogue existed belonged to the default scope.
		if (registration.ServerTags.Contains(JobTags.Default, StringComparer.Ordinal))
		{
			var knownNames = existingDefinitions.Select(static definition => definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
			managedNames.UnionWith(existingSchedules
				.Where(schedule => schedule.IsCodeDefined && !knownNames.Contains(schedule.JobName))
				.Select(static schedule => schedule.JobName));
		}

		managedNames.UnionWith(desired.Keys);

		var existing = existingSchedules.ToDictionary(static schedule => schedule.Name, StringComparer.Ordinal);
		var schedules = new List<RecurringJobSchedule>();
		var scheduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var suppliedSchedule in registration.RecurringSchedules)
		{
			var schedule = suppliedSchedule;
			if (!supplied.TryGetValue(schedule.JobName, out var definition)
				|| !schedule.IsCodeDefined
				|| !string.Equals(schedule.Name, definition.Name, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(schedule.Cron, definition.Cron, StringComparison.Ordinal)
				|| !string.Equals(schedule.TimeZone, definition.TimeZone, StringComparison.Ordinal)
				|| !string.Equals(schedule.QueueName, definition.QueueName, StringComparison.Ordinal)
				|| !scheduleNames.Add(schedule.Name))
				throw new ArgumentException("Code-defined schedules must match their supplied job definitions.", nameof(registration));
			if (!desired.TryGetValue(schedule.JobName, out var managedDefinition))
				continue;
			schedule = schedule with { Name = managedDefinition.Name, JobName = managedDefinition.Name };
			if (existing.TryGetValue(schedule.Name, out var current))
			{
				schedules.Add(schedule with
				{
					IsPaused = current.IsPaused,
					LastRunAt = current.LastRunAt,
					NextRunAt = string.Equals(current.Cron, schedule.Cron, StringComparison.Ordinal)
						&& string.Equals(current.TimeZone, schedule.TimeZone, StringComparison.Ordinal)
						? current.NextRunAt : schedule.NextRunAt,
				});
			}
			else
				schedules.Add(schedule);
		}

		if (desired.Values.Any(definition => definition.Cron is not null && !scheduleNames.Contains(definition.Name)))
			throw new ArgumentException("Every managed cron definition must supply its code-defined schedule.", nameof(registration));
		var desiredScheduleNames = schedules.Select(static schedule => schedule.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new()
		{
			Definitions = [.. desired.Values],
			RemovedDefinitionNames = removedDefinitions,
			Schedules = schedules,
			RemovedScheduleNames = existingSchedules
				.Where(schedule => schedule.IsCodeDefined && managedNames.Contains(schedule.JobName) && !desiredScheduleNames.Contains(schedule.Name))
				.Select(static schedule => schedule.Name).ToList(),
		};
	}
}

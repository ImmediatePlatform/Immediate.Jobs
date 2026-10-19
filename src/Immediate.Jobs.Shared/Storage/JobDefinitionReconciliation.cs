using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Storage-neutral changes computed inside a provider's atomic startup operation.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record JobDefinitionReconciliation
{
	/// <summary>
	/// 	The cancellation reason recorded when a job's persisted definition is removed.
	/// </summary>
	public const string RemovalCancellationReason = "Job definition removed.";

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
	/// 	Computes changes against the provider's locked snapshot.
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
	/// <remarks>
	/// 	The scheduler constructs a complete registration with one schedule for each code-defined recurring job.
	/// </remarks>
	[SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Only called with scheduler-created registrations and provider snapshots.")]
	public static JobDefinitionReconciliation Create(
		JobDefinitionRegistration registration,
		IReadOnlyList<JobDefinitionRecord> existingDefinitions,
		IReadOnlyList<RecurringJobSchedule> existingSchedules
	)
	{
		var canonicalNames = existingDefinitions.ToDictionary(static definition => definition.Name, static definition => definition.Name, StringComparer.OrdinalIgnoreCase);
		var desired = registration.Definitions
			.Where(definition => JobTags.Intersect(definition.Tags, registration.ServerTags))
			.Select(definition => canonicalNames.TryGetValue(definition.Name, out var name) ? definition with { Name = name } : definition)
			.ToDictionary(static definition => definition.Name, StringComparer.OrdinalIgnoreCase);
		var managedNames = existingDefinitions
			.Where(definition => JobTags.Intersect(definition.Tags, registration.ServerTags))
			.Select(static definition => definition.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var removedDefinitions = managedNames.Where(name => !desired.ContainsKey(name)).ToList();

		managedNames.UnionWith(desired.Keys);

		var existing = existingSchedules.ToDictionary(static schedule => schedule.Name, StringComparer.OrdinalIgnoreCase);
		var schedules = new List<RecurringJobSchedule>();
		foreach (var (jobName, nextRunAt) in registration.RecurringSchedules)
		{
			if (!desired.TryGetValue(jobName, out var managedDefinition))
				continue;
			var schedule = new RecurringJobSchedule
			{
				Name = managedDefinition.Name,
				JobName = managedDefinition.Name,
				Cron = managedDefinition.Cron!,
				TimeZone = managedDefinition.TimeZone,
				IsCodeDefined = true,
				NextRunAt = nextRunAt,
			};
			if (existing.TryGetValue(schedule.Name, out var current))
			{
				var resetCancellation = !current.IsCodeDefined && current.CancellationReason is not null;
				schedules.Add(schedule with
				{
					Name = current.Name,
					IsPaused = current.IsPaused && !resetCancellation,
					CancellationReason = resetCancellation ? null : current.CancellationReason,
					LastRunAt = current.LastRunAt,
					NextRunAt = string.Equals(current.Cron, schedule.Cron, StringComparison.Ordinal)
						&& string.Equals(current.TimeZone, schedule.TimeZone, StringComparison.Ordinal)
						? current.NextRunAt : schedule.NextRunAt,
				});
			}
			else
				schedules.Add(schedule);
		}

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

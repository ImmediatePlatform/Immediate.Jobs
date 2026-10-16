using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	/// <inheritdoc />
	public async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
	{
		SingleServerMergeJobDefinitionsListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);
		try
		{
			await DurableStorage.MergeJobDefinitionsListAsync(registration, cancellationToken);
			// Refresh the entire durable catalogue of schedules, from durable storage.
			var snapshot = await DurableStorage.GetMonitoringSnapshotAsync(cancellationToken);
			var monitoringDefinitions = await DurableStorage.GetMonitoringDefinitionsAsync(cancellationToken);
			var definitions = monitoringDefinitions.Jobs;
			var statuses = snapshot.Recurring.ToDictionary(static status => status.Name, StringComparer.OrdinalIgnoreCase);
			var schedules = monitoringDefinitions.Recurring.Where(definition => statuses.ContainsKey(definition.Name))
				.Select(definition => RecurringJobSchedule.FromDefinition(definition, statuses[definition.Name])).ToList();
			var names = definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var primaryDefinitions = await PrimaryStorage.GetJobDefinitionsAsync(cancellationToken);
			var tags = definitions.Concat(primaryDefinitions).SelectMany(static definition => definition.Tags)
				.Append(JobTags.Default).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			await PrimaryStorage.MergeJobDefinitionsListAsync(
				new()
				{
					Definitions = definitions,
					ServerTags = tags,
					RecurringSchedules = schedules.Where(schedule => schedule.IsCodeDefined && names.Contains(schedule.JobName)).ToList(),
				},
				cancellationToken
			);
			foreach (var schedule in schedules.Where(static schedule => !schedule.IsCodeDefined))
				await PrimaryStorage.UpsertRecurringAsync(schedule, cancellationToken);
			// Code-defined schedules without metadata still belong in the primary's monitoring snapshot.
			foreach (var schedule in schedules.Where(schedule => schedule.IsCodeDefined && !names.Contains(schedule.JobName)))
				await PrimaryStorage.UpsertRecurringAsync(schedule, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobDefinition>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		SingleServerGetJobDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await DurableStorage.GetJobDefinitionsAsync(cancellationToken);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerMergeJobDefinitionsListAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerMergeJobDefinitionsListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SingleServerMergeJobDefinitionsListAsyncCalled"
	)]
	private partial void SingleServerMergeJobDefinitionsListAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerGetJobDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerGetJobDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SingleServerGetJobDefinitionsAsyncCalled"
	)]
	private partial void SingleServerGetJobDefinitionsAsyncCalled();
}

using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	private readonly Dictionary<string, JobDefinitionRecord> _jobDefinitions = [with(StringComparer.OrdinalIgnoreCase)];

	/// <inheritdoc />
	public async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
	{
		InMemoryMergeJobDefinitionsListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		lock (_gate)
		{
			var changes = JobDefinitionReconciliation.Create(registration, [.. _jobDefinitions.Values], [.. _recurring.Values]);
			foreach (var name in changes.RemovedDefinitionNames)
				_jobDefinitions.Remove(name);
			foreach (var definition in changes.Definitions)
				_jobDefinitions[definition.Name] = definition;
			foreach (var name in changes.RemovedScheduleNames)
				_recurring.Remove(name);
			foreach (var schedule in changes.Schedules)
				_recurring[schedule.Name] = schedule;
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		InMemoryGetJobDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		lock (_gate)
			return [.. _jobDefinitions.Values.OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)];
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryMergeJobDefinitionsListAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryMergeJobDefinitionsListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "InMemoryMergeJobDefinitionsListAsyncCalled"
	)]
	private partial void InMemoryMergeJobDefinitionsListAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryGetJobDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryGetJobDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "InMemoryGetJobDefinitionsAsyncCalled"
	)]
	private partial void InMemoryGetJobDefinitionsAsyncCalled();
}

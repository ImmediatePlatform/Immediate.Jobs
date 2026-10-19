using System.Runtime.InteropServices;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
	{
		InMemoryMergeJobDefinitionsListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var existingDefinitionsToRemove = _jobDefinitions
				.Where(jd => JobTags.Intersect(jd.Value.Tags, registration.ServerTags))
				.Select(jd => jd.Key)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			var existingSchedulesToRemove = _recurring
				.Where(r => r.Value.IsCodeDefined)
				.ToDictionary(r => r.Key, r => _jobDefinitions.TryGetValue(r.Value.JobName, out var def) ? def.Tags : [], StringComparer.OrdinalIgnoreCase);

			var existingSchedulesToCancel = _recurring
				.Where(r => !r.Value.IsCodeDefined)
				.GroupBy(r => r.Value.JobName, r => r.Value, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(r => r.Key, r => r.ToList(), StringComparer.OrdinalIgnoreCase);

			foreach (var definition in registration.Definitions.Where(d => JobTags.Intersect(d.Tags, registration.ServerTags)))
			{
				existingDefinitionsToRemove.Remove(definition.Name);

				ref var definitionRecord = ref CollectionsMarshal.GetValueRefOrAddDefault(_jobDefinitions, definition.Name, out _);
				var originalName = definitionRecord?.Name ?? definition.Name;
				definitionRecord = definition with { Name = originalName };

				if (registration.RecurringSchedules.TryGetValue(definition.Name, out var nextRunAt))
				{
					existingSchedulesToRemove.Remove(definition.Name);

					ref var recurring = ref CollectionsMarshal.GetValueRefOrAddDefault(_recurring, definition.Name, out _);

					recurring = new()
					{
						Name = recurring?.Name ?? originalName,
						JobName = originalName,
						Cron = definition.Cron!,
						TimeZone = definition.TimeZone,
						IsCodeDefined = true,
						IsPaused = recurring is { IsPaused: true, CancellationReason: null },
						LastRunAt = recurring is { } ? recurring.LastRunAt : null,

						NextRunAt =
							recurring is { }
							&& string.Equals(recurring.Cron, definition.Cron, StringComparison.Ordinal)
							&& string.Equals(recurring.TimeZone, definition.TimeZone, StringComparison.Ordinal)
							? recurring.NextRunAt
							: nextRunAt,
					};
				}
			}

			foreach (var (name, tags) in existingSchedulesToRemove)
			{
				// if we're here, the schedule is active on the current server tags
				// but possibly old definition referenced the current server tags
				// so keep tags from original definition and double-check before dropping
				if (JobTags.Intersect(tags, registration.ServerTags))
					_recurring.Remove(name);
			}

			var now = timeProvider.GetUtcNow();

			// Mark every affected invocation before settling dependencies between them.
			var cancelledJobs = new List<JobRecord>();

			foreach (var jobName in existingDefinitionsToRemove)
			{
				_jobDefinitions.Remove(jobName);

				if (!_jobsByDefinition.TryGetValue(jobName, out var handles))
					handles = [];

				var schedules = existingSchedulesToCancel.GetValueOrDefault(jobName, []);

				DefinitionRemoved(
					jobName,
					handles.Count,
					schedules.Count
				);

				foreach (var jobHandle in handles)
				{
					ref var job = ref ValidateExistingElement(_jobs, jobHandle);

					if (IsTerminal(job.State))
						continue;

					if (job.State == JobState.Active)
						CompleteExecution(job, JobExecutionState.Cancelled, now, JobDefinitionReconciliation.RemovalCancellationReason);

					job = job with
					{
						State = JobState.Cancelled,
						CancellationReason = JobDefinitionReconciliation.RemovalCancellationReason,
						CompletedAt = now,
						WorkerId = null,
						LeaseExpiresAt = null,
					};

					cancelledJobs.Add(job);
				}

				foreach (var schedule in schedules)
					_recurring[schedule.Name] = schedule with { IsPaused = true, CancellationReason = JobDefinitionReconciliation.RemovalCancellationReason };
			}

			foreach (var job in cancelledJobs)
			{
				UpdateBatchAfterTerminal(job.BatchHandle, JobState.Cancelled, now);
				ProcessTerminalJob(job.JobHandle);
				RemoveFairQueueCursorWhenBacklogClears(GetQueueName(job.JobName), job.GroupId);
			}

			foreach (var (queue, group) in _fairQueueLastServed.Keys.ToList())
				RemoveFairQueueCursorWhenBacklogClears(queue, group);
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

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private string? GetQueueName(string jobName) =>
		_jobDefinitions.TryGetValue(jobName, out var definition) ? definition.QueueName : null;

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private JobRecord ReadRecord(JobRecord job) => job with { QueueName = GetQueueName(job.JobName) };

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryDefinitionRemoved,
		EventName = "Immediate.Jobs.Shared.DefinitionRemoved",
		Level = LogLevel.Warning,
		Message = "Job definition '{JobName}' removed; canceled {JobCount} invocations and {ScheduleCount} recurring schedules."
	)]
	private partial void DefinitionRemoved(string jobName, int jobCount, int scheduleCount);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryMergeJobDefinitionsListAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryMergeJobDefinitionsListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "InMemoryMergeJobDefinitionsListAsyncCalled"
	)]
	private partial void InMemoryMergeJobDefinitionsListAsyncCalled();

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryGetJobDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryGetJobDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "InMemoryGetJobDefinitionsAsyncCalled"
	)]
	private partial void InMemoryGetJobDefinitionsAsyncCalled();
}

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
			var removed = changes.RemovedDefinitionNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var jobs = _jobs.Values.Where(job => removed.Contains(job.JobName) && !IsTerminal(job.State)).ToList();
			var schedules = _recurring.Values.Where(schedule => !schedule.IsCodeDefined && removed.Contains(schedule.JobName)
				&& schedule.CancellationReason is null).ToList();
			var now = timeProvider.GetUtcNow();
			// Mark every affected invocation before settling dependencies between them.
			foreach (var job in jobs)
			{
				if (job.State == JobState.Active)
					CompleteExecution(job, JobExecutionState.Cancelled, now, JobDefinitionReconciliation.RemovalCancellationReason);
				_jobs[job.JobHandle] = job with
				{
					State = JobState.Cancelled,
					CancellationReason = JobDefinitionReconciliation.RemovalCancellationReason,
					CompletedAt = now,
					WorkerId = null,
					LeaseExpiresAt = null,
				};
			}

			foreach (var job in jobs)
			{
				UpdateBatchAfterTerminal(job.BatchHandle, JobState.Cancelled, now);
				ProcessTerminalJob(job.JobHandle);
				RemoveFairQueueCursorWhenBacklogClears(GetQueueName(job.JobName), job.GroupId);
			}

			foreach (var schedule in schedules)
				_recurring[schedule.Name] = schedule with { IsPaused = true, CancellationReason = JobDefinitionReconciliation.RemovalCancellationReason };
			foreach (var name in changes.RemovedDefinitionNames)
			{
				_jobDefinitions.Remove(name);
				DefinitionRemoved(name, jobs.Count(job => string.Equals(job.JobName, name, StringComparison.OrdinalIgnoreCase)),
					schedules.Count(schedule => string.Equals(schedule.JobName, name, StringComparison.OrdinalIgnoreCase)));
			}

			foreach (var definition in changes.Definitions)
				_jobDefinitions[definition.Name] = definition;

			foreach (var (queue, group) in _fairQueueLastServed.Keys.ToList())
				RemoveFairQueueCursorWhenBacklogClears(queue, group);

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

	internal void RestoreRecurringSchedules(IReadOnlyList<RecurringJobSchedule> schedules)
	{
		lock (_gate)
		{
			foreach (var schedule in schedules)
				_recurring[schedule.Name] = schedule;
		}
	}

	private string? GetQueueName(string jobName) =>
		_jobDefinitions.TryGetValue(jobName, out var definition) ? definition.QueueName : null;

	private JobRecord ReadRecord(JobRecord job) => job with { QueueName = GetQueueName(job.JobName) };

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryDefinitionRemoved,
		EventName = "Immediate.Jobs.Shared.DefinitionRemoved",
		Level = LogLevel.Warning,
		Message = "Job definition '{JobName}' removed; canceled {JobCount} invocations and {ScheduleCount} recurring schedules."
	)]
	private partial void DefinitionRemoved(string jobName, int jobCount, int scheduleCount);

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

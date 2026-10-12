using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask UpdatePayloadAsync(
		JobHandle jobHandle,
		string expectedJobName,
		string payload,
		CancellationToken cancellationToken = default
	)
	{
		UpdatePayloadAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var job = GetTriggerable(jobHandle, expectedJobName);
			if (job.State != JobState.WaitingForTrigger)
				throw new ImmediateJobException($"Job '{jobHandle}' is not waiting for a trigger.");

			_jobs[jobHandle] = job with { Payload = payload };
		}
	}

	/// <inheritdoc />
	public async ValueTask<bool> TryTriggerAsync(
		JobHandle jobHandle,
		string expectedJobName,
		DateTimeOffset dueAt,
		CancellationToken cancellationToken = default
	)
	{
		TryTriggerAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var job = GetTriggerable(jobHandle, expectedJobName);
			if (job.BatchHandle is not null && _batches[job.BatchHandle].State == BatchState.WaitingForTrigger)
				throw new ImmediateJobException($"Job '{jobHandle}' belongs to batch '{job.BatchHandle}', which is waiting for a trigger; trigger the batch instead.");
			if (job.State != JobState.WaitingForTrigger)
				return false;
			if (job.Payload.Length == 0)
				throw new ImmediateJobException($"Job '{jobHandle}' cannot be triggered before its parameters are supplied.");

			Trigger(job, dueAt);
			return true;
		}
	}

	/// <inheritdoc />
	public async ValueTask<bool> TryTriggerBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		TryTriggerBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchHandle, out var batch))
				throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
			if (batch.State != BatchState.WaitingForTrigger)
				return false;

			_batches[batchHandle] = batch with { State = BatchState.Executing };
			foreach (var job in _jobs.Values
				.Where(job => job.BatchHandle == batchHandle && job.State == JobState.WaitingForTrigger)
				.ToList())
			{
				Trigger(job, job.DueAt);
			}

			return true;
		}
	}

	private JobRecord GetTriggerable(JobHandle jobHandle, string expectedJobName)
	{
		if (!_jobs.TryGetValue(jobHandle, out var job))
			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (!string.Equals(job.JobName, expectedJobName, StringComparison.OrdinalIgnoreCase))
			throw new ImmediateJobException($"Job '{jobHandle}' is not a '{expectedJobName}' job.");

		return job;
	}

	private void Trigger(JobRecord job, DateTimeOffset dueAt)
	{
		if (job.RemainingDependencies != 0)
		{
			_jobs[job.JobHandle] = job with { State = JobState.AwaitingContinuation, DueAt = dueAt };
			return;
		}

		var now = timeProvider.GetUtcNow();
		if (_edges.Any(edge => edge.ChildJobHandle == job.JobHandle))
		{
			var (triggersSatisfied, _, delay) = EvaluateIncomingTriggers(job.JobHandle);
			if (!triggersSatisfied)
			{
				TransitionToTerminal(job.JobHandle, JobState.Skipped, error: null, now);
				return;
			}

			dueAt = dueAt > now + delay ? dueAt : now + delay;
		}

		_jobs[job.JobHandle] = job with
		{
			State = dueAt <= now ? JobState.Pending : JobState.Scheduled,
			DueAt = dueAt,
		};
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryUpdatePayloadAsyncCalled,
		EventName = "Immediate.Jobs.Shared.UpdatePayloadAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpdatePayloadAsync called (JobHandle={JobHandle})"
	)]
	private partial void UpdatePayloadAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryTryTriggerAsyncCalled,
		EventName = "Immediate.Jobs.Shared.TryTriggerAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerAsync called (JobHandle={JobHandle})"
	)]
	private partial void TryTriggerAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryTryTriggerBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.TryTriggerBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void TryTriggerBatchAsyncCalled(BatchHandle batchHandle);
}

using System.Diagnostics.CodeAnalysis;
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
			ref var job = ref GetTriggerable(jobHandle, expectedJobName);

			if (job.State != JobState.WaitingForTrigger)
				throw new ImmediateJobException($"Job '{jobHandle}' is not waiting for a trigger.");

			UpdateJob(ref job, job with { Payload = payload });
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
			ref var job = ref GetTriggerable(jobHandle, expectedJobName);

			if (job.BatchHandle is not null && _batches[job.BatchHandle].State == BatchState.WaitingForTrigger)
				throw new ImmediateJobException($"Job '{jobHandle}' belongs to batch '{job.BatchHandle}', which is waiting for a trigger; trigger the batch instead.");

			if (job.Payload.Length == 0)
				throw new ImmediateJobException($"Job '{jobHandle}' cannot be triggered before its parameters are supplied.");

			if (job.State != JobState.WaitingForTrigger)
				return false;

			Trigger(ref job, dueAt);
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
			ref var batch = ref ValidateExistingElement(_batches, batchHandle);

			if (batch.State != BatchState.WaitingForTrigger)
				return false;

			batch = batch with { State = BatchState.Executing };

			if (_jobsByBatch.TryGetValue(batchHandle, out var handles))
			{
				foreach (var handle in handles)
				{
					ref var job = ref ValidateExistingElement(_jobs, handle);
					if (job.State == JobState.WaitingForTrigger)
						Trigger(ref job, job.DueAt);
				}
			}

			return true;
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and while using the returned reference.
	/// 	The dictionary must not be structurally modified while the reference is in use.
	/// </remarks>
	[SuppressMessage("Style", "IDE0007:Use implicit type", Justification = "Interaction between IDE0007 and CS8619")]
	private ref JobRecord GetTriggerable(JobHandle jobHandle, string expectedJobName)
	{
		ref JobRecord job = ref ValidateExistingElement(_jobs, jobHandle);

		if (!string.Equals(job.JobName, expectedJobName, StringComparison.OrdinalIgnoreCase))
			throw new ImmediateJobException($"Job '{jobHandle}' is not a '{expectedJobName}' job.");

		return ref job;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void Trigger(ref JobRecord job, DateTimeOffset dueAt)
	{
		if (job.RemainingDependencies != 0)
		{
			UpdateJob(ref job, job with { State = JobState.AwaitingContinuation, DueAt = dueAt });
			return;
		}

		var jobId = job.JobHandle;
		var now = timeProvider.GetUtcNow();

		if (_edgesByChildJob.ContainsKey(jobId))
		{
			var (triggersSatisfied, _, delay) = EvaluateIncomingTriggers(job.JobHandle);

			if (!triggersSatisfied)
			{
				TransitionToTerminal(ref job, JobState.Skipped, error: null, now);
				return;
			}

			dueAt = dueAt > now + delay ? dueAt : now + delay;
		}

		UpdateJob(ref job, job with
		{
			State = dueAt <= now ? JobState.Pending : JobState.Scheduled,
			DueAt = dueAt,
		});
	}

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryUpdatePayloadAsyncCalled,
		EventName = "Immediate.Jobs.Shared.UpdatePayloadAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpdatePayloadAsync called (JobHandle={JobHandle})"
	)]
	private partial void UpdatePayloadAsyncCalled(JobHandle jobHandle);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryTryTriggerAsyncCalled,
		EventName = "Immediate.Jobs.Shared.TryTriggerAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerAsync called (JobHandle={JobHandle})"
	)]
	private partial void TryTriggerAsyncCalled(JobHandle jobHandle);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryTryTriggerBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.TryTriggerBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void TryTriggerBatchAsyncCalled(BatchHandle batchHandle);
}

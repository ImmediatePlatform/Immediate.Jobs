using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

			job = job with { Payload = payload };
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
			if (job.State != JobState.WaitingForTrigger)
				return false;
			if (job.Payload.Length == 0)
				throw new ImmediateJobException($"Job '{jobHandle}' cannot be triggered before its parameters are supplied.");

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
			ref var batch = ref CollectionsMarshal.GetValueRefOrNullRef(_batches, batchHandle);
			if (Unsafe.IsNullRef(ref batch))
				throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
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
	/// 	The caller must hold the <c>_gate</c> lock throughout this call and while using the returned reference.
	/// 	The dictionary must not be structurally modified while the reference is in use.
	/// </remarks>
	[SuppressMessage("Style", "IDE0007:Use implicit type", Justification = "Interaction between IDE0007 and CS8619")]
	private ref JobRecord GetTriggerable(JobHandle jobHandle, string expectedJobName)
	{
		ref JobRecord job = ref CollectionsMarshal.GetValueRefOrNullRef(_jobs, jobHandle);
		if (Unsafe.IsNullRef(ref job))
			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (!string.Equals(job.JobName, expectedJobName, StringComparison.OrdinalIgnoreCase))
			throw new ImmediateJobException($"Job '{jobHandle}' is not a '{expectedJobName}' job.");

		return ref job;
	}

	/// <remarks>
	/// 	The caller must hold the <c>_gate</c> lock.
	/// </remarks>
	private void Trigger(ref JobRecord job, DateTimeOffset dueAt)
	{
		if (job.RemainingDependencies != 0)
		{
			job = job with { State = JobState.AwaitingContinuation, DueAt = dueAt };
			return;
		}

		var now = timeProvider.GetUtcNow();
		var jobHandle = job.JobHandle;
		if (_edges.Any(edge => edge.ChildJobHandle == jobHandle))
		{
			var (triggersSatisfied, _, delay) = EvaluateIncomingTriggers(job.JobHandle);
			if (!triggersSatisfied)
			{
				TransitionToTerminal(ref job, JobState.Skipped, error: null, now);
				return;
			}

			dueAt = dueAt > now + delay ? dueAt : now + delay;
		}

		job = job with
		{
			State = dueAt <= now ? JobState.Pending : JobState.Scheduled,
			DueAt = dueAt,
		};
	}

	/// <remarks>
	/// 	The caller does not need to hold the <c>_gate</c> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryUpdatePayloadAsyncCalled,
		EventName = "Immediate.Jobs.Shared.UpdatePayloadAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpdatePayloadAsync called (JobHandle={JobHandle})"
	)]
	private partial void UpdatePayloadAsyncCalled(JobHandle jobHandle);

	/// <remarks>
	/// 	The caller does not need to hold the <c>_gate</c> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryTryTriggerAsyncCalled,
		EventName = "Immediate.Jobs.Shared.TryTriggerAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerAsync called (JobHandle={JobHandle})"
	)]
	private partial void TryTriggerAsyncCalled(JobHandle jobHandle);

	/// <remarks>
	/// 	The caller does not need to hold the <c>_gate</c> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryTryTriggerBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.TryTriggerBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void TryTriggerBatchAsyncCalled(BatchHandle batchHandle);
}

using Immediate.Jobs.Shared.Apis;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
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

		await RetryConcurrencyAsync(
			connection => UpdatePayloadCoreAsync(connection, jobHandle, expectedJobName, payload, cancellationToken),
			cancellationToken
		);
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

		var triggered = false;
		var terminalGroups = new HashSet<(string QueueName, string GroupId)>();
		await RetryConcurrencyAsync(
			async connection => triggered = await TryTriggerCoreAsync(
				connection,
				jobHandle,
				expectedJobName,
				dueAt,
				terminalGroups,
				cancellationToken
			),
			cancellationToken
		);
		await CleanupFairQueueGroupsAsync(terminalGroups);
		return triggered;
	}

	/// <inheritdoc />
	public async ValueTask<bool> TryTriggerBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		TryTriggerBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var triggered = false;
		var terminalGroups = new HashSet<(string QueueName, string GroupId)>();
		await RetryConcurrencyAsync(
			async connection => triggered = await TryTriggerBatchCoreAsync(
				connection,
				batchHandle,
				terminalGroups,
				cancellationToken
			),
			cancellationToken
		);
		await CleanupFairQueueGroupsAsync(terminalGroups);
		return triggered;
	}

	private async Task UpdatePayloadCoreAsync(
		DataConnection connection,
		JobHandle jobHandle,
		string expectedJobName,
		string payload,
		CancellationToken cancellationToken
	)
	{
		var job = await GetTriggerableAsync(connection, jobHandle, expectedJobName, cancellationToken);
		if (job.State != JobState.WaitingForTrigger)
			throw new ImmediateJobException($"Job '{jobHandle}' is not waiting for a trigger.");

		var oldStamp = job.ConcurrencyStamp;
		job.Payload = payload;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
			throw new LostRaceException();
	}

	private async Task<bool> TryTriggerCoreAsync(
		DataConnection connection,
		JobHandle jobHandle,
		string expectedJobName,
		DateTimeOffset dueAt,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var job = await GetTriggerableAsync(connection, jobHandle, expectedJobName, cancellationToken);
		if (job.BatchHandle is not null && await Batches(connection)
			.AnyAsync(batch => batch.Id == job.BatchHandle && batch.State == BatchState.WaitingForTrigger, cancellationToken))
		{
			throw new ImmediateJobException($"Job '{jobHandle}' belongs to batch '{job.BatchHandle}', which is waiting for a trigger; trigger the batch instead.");
		}

		if (job.State != JobState.WaitingForTrigger)
			return false;
		if (job.Payload.Length == 0)
			throw new ImmediateJobException($"Job '{jobHandle}' cannot be triggered before its parameters are supplied.");

		await TriggerWaitingJobAsync(connection, job, dueAt, timeProvider.GetUtcNow(), terminalGroups, cancellationToken);
		return true;
	}

	private async Task<ImmediateJobEntity> GetTriggerableAsync(
		DataConnection connection,
		JobHandle jobHandle,
		string expectedJobName,
		CancellationToken cancellationToken
	)
	{
		var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (!string.Equals(job.JobName, expectedJobName, StringComparison.OrdinalIgnoreCase))
			throw new ImmediateJobException($"Job '{jobHandle}' is not a '{expectedJobName}' job.");

		return job;
	}

	private async Task TriggerWaitingJobAsync(
		DataConnection connection,
		ImmediateJobEntity job,
		DateTimeOffset dueAt,
		DateTimeOffset now,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var oldStamp = job.ConcurrencyStamp;
		var skipped = false;
		job.DueAt = dueAt;
		if (job.RemainingDependencies != 0)
		{
			job.State = JobState.AwaitingContinuation;
		}
		else if (await ShouldSkipSettledContinuationAsync(connection, job.Id, cancellationToken))
		{
			job.State = JobState.Skipped;
			job.CompletedAt = now;
			skipped = true;
		}
		else
		{
			var delayedDueAt = now + await GetMaximumContinuationDelayAsync(connection, job.Id, cancellationToken);
			if (job.DueAt < delayedDueAt)
				job.DueAt = delayedDueAt;
			job.State = job.DueAt <= now ? JobState.Pending : JobState.Scheduled;
		}

		job.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
			throw new LostRaceException();
		if (skipped)
			await PropagateTerminalAsync(connection, job, now, terminalGroups, cancellationToken);
	}

	private async Task<bool> TryTriggerBatchCoreAsync(
		DataConnection connection,
		BatchHandle batchHandle,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
		if (batch.State != BatchState.WaitingForTrigger)
			return false;

		var batchStamp = batch.ConcurrencyStamp;
		batch.State = BatchState.Executing;
		batch.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken))
			throw new LostRaceException();

		var jobs = await Jobs(connection)
			.Where(job => job.BatchHandle == batchHandle.Value && job.State == JobState.WaitingForTrigger)
			.OrderBy(job => job.Id)
			.ToListAsync(cancellationToken);
		foreach (var job in jobs)
			await TriggerWaitingJobAsync(connection, job, job.DueAt, now, terminalGroups, cancellationToken);

		return true;
	}

	[LoggerMessage(
		EventId = LibraryEventIds.UpdatePayloadAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.UpdatePayloadAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpdatePayloadAsync called (JobHandle={JobHandle})"
	)]
	private partial void UpdatePayloadAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.TryTriggerAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.TryTriggerAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerAsync called (JobHandle={JobHandle})"
	)]
	private partial void TryTriggerAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.TryTriggerBatchAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.TryTriggerBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void TryTriggerBatchAsyncCalled(BatchHandle batchHandle);
}

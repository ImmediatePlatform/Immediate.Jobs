using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		CompleteAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = _timeProvider.GetUtcNow();
		var result = await EvaluateInt64Async(
			RedisScripts.Complete,
			[
				JobKey(jobHandle),
				LeasesKey,
				StateKey(JobState.Active),
				StateKey(JobState.Succeeded),
				CompletedKey(JobState.Succeeded),
				ExecutionIndexKey(jobHandle),
				ExecutionDataKey(jobHandle),
			],
			[workerId, executionNumber, Ticks(now), jobHandle.Value, Score(now), _root],
			cancellationToken
		);
		ThrowIfNotOwned(result, jobHandle, workerId);
	}

	/// <inheritdoc />
	public async ValueTask FailAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		FailAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = _timeProvider.GetUtcNow();
		var nextTicks = nextRetryAt is { } retryAt ? Ticks(retryAt) : "";
		var nextScore = nextRetryAt is { } retryScore ? Score(retryScore) : 0;
		var result = await EvaluateInt64Async(
			RedisScripts.Fail,
			[
				JobKey(jobHandle),
				LeasesKey,
				StateKey(JobState.Active),
				StateKey(JobState.Failed),
				CompletedKey(JobState.Failed),
				StateKey(JobState.Scheduled),
				StateKey(JobState.Pending),
				ExecutionIndexKey(jobHandle),
				ExecutionDataKey(jobHandle),
			],
			[
				workerId,
				executionNumber,
				jobHandle.Value,
				nextTicks,
				error,
				Ticks(now),
				Score(now),
				nextScore,
				Score(now),
				_root,
			],
			cancellationToken
		);
		ThrowIfNotOwned(result, jobHandle, workerId);
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		CancelAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = _timeProvider.GetUtcNow();
		var result = await EvaluateInt64Async(
			RedisScripts.Cancel,
			[JobKey(jobHandle), LeasesKey, ExecutionIndexKey(jobHandle), ExecutionDataKey(jobHandle)],
			[jobHandle.Value, Ticks(now), Score(now), _root],
			cancellationToken
		);
		if (result == 0)
			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (result < 0)
			throw new ImmediateJobException("Only a non-terminal job can be cancelled.");
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		RetryAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = _timeProvider.GetUtcNow();
		var result = await EvaluateInt64Async(
			RedisScripts.Retry,
			[
				JobKey(jobHandle),
				StateKey(JobState.Failed),
				StateKey(JobState.Scheduled),
				StateKey(JobState.Pending),
				CompletedKey(JobState.Failed),
				ExecutionIndexKey(jobHandle),
				ExecutionDataKey(jobHandle),
			],
			[Ticks(now), Score(now), jobHandle.Value, _root],
			cancellationToken
		);
		if (result == 0)
			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (result < 0)
			throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		DeleteAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var result = await EvaluateInt64Async(
			RedisScripts.Delete,
			[JobKey(jobHandle), AllJobsKey, RecurringDedupeKey, ExecutionIndexKey(jobHandle), ExecutionDataKey(jobHandle)],
			[jobHandle.Value, _root],
			cancellationToken
		);
		if (result == 0)
			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (result < 0)
			throw new ImmediateJobException("Only terminal jobs can be deleted.");
	}

	/// <inheritdoc />
	public async ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		PurgeJobsAsyncCalled(succeededRetention, failedRetention);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = _timeProvider.GetUtcNow();
		await PurgeStateAsync(JobState.Succeeded, now - succeededRetention, cancellationToken);
		await PurgeStateAsync(JobState.Failed, now - failedRetention, cancellationToken);
		await PurgeStateAsync(JobState.Cancelled, now - failedRetention, cancellationToken);
		await PurgeStateAsync(JobState.Skipped, now - failedRetention, cancellationToken);
	}

	private async Task PurgeStateAsync(
		JobState state,
		DateTimeOffset cutoff,
		CancellationToken cancellationToken
	)
	{
		while (true)
		{
			var ids = await Database.SortedSetRangeByScoreAsync(
				CompletedKey(state),
				stop: Score(cutoff),
				exclude: Exclude.Stop,
				take: 256
			).WaitAsync(cancellationToken);
			if (ids.Length == 0)
				return;
			foreach (var value in ids)
			{
				var id = JobHandle.FromString((string)value!);

				_ = await EvaluateInt64Async(
					RedisScripts.Purge,
					[
						JobKey(id),
						CompletedKey(state),
						AllJobsKey,
						StateKey(state),
						RecurringDedupeKey,
						ExecutionIndexKey(id),
						ExecutionDataKey(id),
					],
					[value, (int)state],
					cancellationToken
				);
			}
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.CompleteAsyncCalled,
		EventName = "Immediate.Jobs.Redis.CompleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.FailAsyncCalled,
		EventName = "Immediate.Jobs.Redis.FailAsyncCalled",
		Level = LogLevel.Debug,
		Message = "FailAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void FailAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.CancelAsyncCalled,
		EventName = "Immediate.Jobs.Redis.CancelAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelAsync called (JobHandle={JobHandle})"
	)]
	private partial void CancelAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.RetryAsyncCalled,
		EventName = "Immediate.Jobs.Redis.RetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RetryAsync called (JobHandle={JobHandle})"
	)]
	private partial void RetryAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.DeleteAsyncCalled,
		EventName = "Immediate.Jobs.Redis.DeleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DeleteAsync called (JobHandle={JobHandle})"
	)]
	private partial void DeleteAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.PurgeJobsAsyncCalled,
		EventName = "Immediate.Jobs.Redis.PurgeJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeJobsAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeJobsAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);
}

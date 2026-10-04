using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		EnqueueAsyncCalled(job.JobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		ValidateQueueJob(job);
		var result = await EvaluateInt64Async(
			RedisScripts.Enqueue,
			[JobKey(job.JobHandle), AllJobsKey, StateKey(job.State), DueKey(job.QueueName)],
			CreateEnqueueArguments(job),
			cancellationToken
		);
		if (result == 0)
			throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
	}

	private RedisValue[] CreateEnqueueArguments(JobRecord job) =>
	[
		JsonSerializer.Serialize(job, RedisJsonSerializerContext.Default.JobRecord),
		(int)job.State,
		Ticks(job.DueAt),
		job.Attempt,
		job.WorkerId ?? "",
		NullableTicks(job.LeaseExpiresAt),
		job.LastError ?? "",
		NullableTicks(job.CompletedAt),
		job.ExecutionTraceId ?? "",
		job.ExecutionSpanId ?? "",
		NullableTicks(job.ExecutionStartedAt),
		job.QueueName,
		job.JobName,
		Score(job.CreatedAt),
		job.JobHandle.Value,
		Score(job.DueAt),
		Ticks(job.CreatedAt),
		DueMember(job),
		job.GroupId ?? "",
		_root,
	];

	private static void ValidateQueueJob(JobRecord job)
	{
		if (job.BatchHandle is not null || job.RemainingDependencies != 0 || job.FailedDependencies != 0)
		{
			throw new NotSupportedException(
				"Batches & continuations require a graph-capable storage provider (a SQL database)."
			);
		}

		if (job.State is not (JobState.Pending or JobState.Scheduled or JobState.WaitingForTrigger))
			throw new ImmediateJobException($"Queue job '{job.JobHandle}' has invalid state '{job.State}'.");
	}

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueAsyncCalled,
		EventName = "Immediate.Jobs.Redis.EnqueueAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueAsync called (JobHandle={JobHandle})"
	)]
	private partial void EnqueueAsyncCalled(JobHandle jobHandle);
}

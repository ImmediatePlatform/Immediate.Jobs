using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobSubmission job, CancellationToken cancellationToken = default)
	{
		EnqueueAsyncCalled(job.JobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		ValidateQueueJob(job);
		var result = await EvaluateInt64Async(
			RedisScripts.Enqueue,
			[JobKey(job.JobHandle), AllJobsKey, StateKey(job.State), DefinitionMetadataKey],
			CreateEnqueueArguments(job),
			cancellationToken
		);
		if (result < 0)
			throw new KeyNotFoundException($"Job definition '{job.JobName}' was not found.");
		if (result == 0)
			throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
	}

	private RedisValue[] CreateEnqueueArguments(JobSubmission job)
	{
		var name = job.JobName.ToUpperInvariant();
		RedisValue metadata = "";
		if (_definitionSnapshots.TryGetValue(name, out var snapshot))
		{
			if (!string.Equals(job.JobName, snapshot.Definition.Name, StringComparison.Ordinal))
				job = job with { JobName = snapshot.Definition.Name };
			metadata = snapshot.Metadata;
		}

		return [
			JsonSerializer.Serialize(job, RedisJsonSerializerContext.Default.JobSubmission),
			(int)job.State,
			Timestamp(job.DueAt),
			0,
			"",
			"",
			"",
			"",
			"",
			"",
			"",
			name,
			Score(job.CreatedAt),
			job.JobHandle.Value,
			Score(job.DueAt),
			Timestamp(job.CreatedAt),
			DueMember(job.DueAt, job.CreatedAt, job.JobHandle),
			job.GroupId ?? "",
			_root,
			metadata,
		];
	}

	private static void ValidateQueueJob(JobSubmission job)
	{
		if (job.BatchHandle is not null || job.RemainingDependencies != 0)
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

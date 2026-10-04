using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
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

		for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
		{
			var values = await Database.HashGetAsync(JobKey(jobHandle), ["record", "state", "jobName"])
				.WaitAsync(cancellationToken);
			if (values[0].IsNull)
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			if (!string.Equals(values[2], expectedJobName, StringComparison.Ordinal))
				throw new ImmediateJobException($"Job '{jobHandle}' is not a '{expectedJobName}' job.");
			if ((int)values[1] != (int)JobState.WaitingForTrigger)
				throw new ImmediateJobException($"Job '{jobHandle}' is not waiting for a trigger.");

			var record = JsonSerializer.Deserialize((string)values[0]!, RedisJsonSerializerContext.Default.JobRecord)!;
			var result = await EvaluateInt64Async(
				RedisScripts.UpdatePayload,
				[JobKey(jobHandle)],
				[values[0], JsonSerializer.Serialize(record with { Payload = payload }, RedisJsonSerializerContext.Default.JobRecord)],
				cancellationToken
			);
			if (result == 1)
				return;
		}

		throw new ImmediateJobException($"Job '{jobHandle}' was changed concurrently while its parameters were being updated.");
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

		var now = _timeProvider.GetUtcNow();
		var result = await EvaluateInt64Async(
			RedisScripts.Trigger,
			[JobKey(jobHandle)],
			[expectedJobName, Ticks(dueAt), Score(dueAt), Score(now), _root, jobHandle.Value],
			cancellationToken
		);
		if (result == 0)
			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (result == -1)
			throw new ImmediateJobException($"Job '{jobHandle}' is not a '{expectedJobName}' job.");
		if (result == -3)
			throw new ImmediateJobException($"Job '{jobHandle}' cannot be triggered before its parameters are supplied.");
		return result == 1;
	}

	[LoggerMessage(
		EventId = LibraryEventIds.UpdatePayloadAsyncCalled,
		EventName = "Immediate.Jobs.Redis.UpdatePayloadAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpdatePayloadAsync called (JobHandle={JobHandle})"
	)]
	private partial void UpdatePayloadAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.TryTriggerAsyncCalled,
		EventName = "Immediate.Jobs.Redis.TryTriggerAsyncCalled",
		Level = LogLevel.Debug,
		Message = "TryTriggerAsync called (JobHandle={JobHandle})"
	)]
	private partial void TryTriggerAsyncCalled(JobHandle jobHandle);

	private const int MaxConcurrencyAttempts = 5;
}

using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	private static readonly string[] ExecutionFieldNames =
	[
		"state",
		"worker",
		"acquired",
		"started",
		"completed",
		"trace",
		"span",
		"error",
		"synthetic",
	];

	/// <inheritdoc />
	public async ValueTask SetExecutionTelemetryAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		SetExecutionTelemetryAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var result = await EvaluateInt64Async(
			RedisScripts.SetTelemetry,
			[JobKey(jobHandle), ExecutionIndexKey(jobHandle), ExecutionDataKey(jobHandle)],
			[workerId, executionNumber, traceId ?? "", spanId ?? "", Timestamp(startedAt)],
			cancellationToken
		);
		ThrowIfNotOwned(result, jobHandle, workerId);
	}

	private static JobExecutionRecord ToExecutionRecord(JobHandle jobHandle, RedisValue[] values) => new()
	{
		JobHandle = jobHandle,
		Attempt = ParseInt32(values[0]),
		State = (JobExecutionState)ParseInt32(values[1]),
		WorkerId = NullIfEmpty(values[2]),
		AcquiredAt = FromNullableTimestamp(values[3]),
		ExecutionStartedAt = FromNullableTimestamp(values[4]),
		CompletedAt = FromNullableTimestamp(values[5]),
		ExecutionTraceId = NullIfEmpty(values[6]),
		ExecutionSpanId = NullIfEmpty(values[7]),
		Error = NullIfEmpty(values[8]),
		IsSynthetic = values[9] == "1",
	};

	[LoggerMessage(
		EventId = LibraryEventIds.SetExecutionTelemetryAsyncCalled,
		EventName = "Immediate.Jobs.Redis.SetExecutionTelemetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SetExecutionTelemetryAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void SetExecutionTelemetryAsyncCalled(JobHandle jobHandle, int execution);

	private static void ThrowIfNotOwned(long result, JobHandle jobHandle, string workerId)
	{
		if (result <= 0)
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
	}
}

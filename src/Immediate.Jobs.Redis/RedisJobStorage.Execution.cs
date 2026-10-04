using System.Globalization;
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
			[workerId, executionNumber, traceId ?? "", spanId ?? "", Ticks(startedAt)],
			cancellationToken
		);
		ThrowIfNotOwned(result, jobHandle, workerId);
	}

	private static RedisValue ExecutionField(int executionNumber, string name) =>
		string.Create(CultureInfo.InvariantCulture, $"{executionNumber}:{name}");

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

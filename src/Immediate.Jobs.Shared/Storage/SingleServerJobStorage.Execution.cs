using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
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
		SingleServerSetExecutionTelemetryAsyncCalled(jobHandle, executionNumber, workerId, traceId, spanId, startedAt);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await DurableStorage
			.SetExecutionTelemetryAsync(
				jobHandle,
				executionNumber,
				workerId,
				traceId,
				spanId,
				startedAt,
				cancellationToken
			);

		await PrimaryStorage
			.SetExecutionTelemetryAsync(
				jobHandle,
				executionNumber,
				workerId,
				traceId,
				spanId,
				startedAt,
				cancellationToken
			);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerSetExecutionTelemetryAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerSetExecutionTelemetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage SetExecutionTelemetryAsync called (JobHandle={JobHandle}, ExecutionNumber={ExecutionNumber}, WorkerId={WorkerId}, TraceId={TraceId}, SpanId={SpanId}, StartedAt={StartedAt})"
	)]
	private partial void SingleServerSetExecutionTelemetryAsyncCalled(JobHandle jobHandle, int executionNumber, string workerId, string? traceId, string? spanId, DateTimeOffset startedAt);
}

using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	/// <inheritdoc />
	public async ValueTask UpdatePayloadAsync(
		JobHandle jobHandle,
		string expectedJobName,
		string payload,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerUpdatePayloadAsyncCalled(jobHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await DurableStorage.UpdatePayloadAsync(jobHandle, expectedJobName, payload, cancellationToken);
			await PrimaryStorage.UpdatePayloadAsync(jobHandle, expectedJobName, payload, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
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
		SingleServerTryTriggerAsyncCalled(jobHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			if (!await DurableStorage.TryTriggerAsync(jobHandle, expectedJobName, dueAt, cancellationToken))
				return false;

			_ = await PrimaryStorage.TryTriggerAsync(jobHandle, expectedJobName, dueAt, cancellationToken);
			return true;
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask<bool> TryTriggerBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		SingleServerTryTriggerBatchAsyncCalled(batchHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			if (!await JobGraphStorage.TryTriggerBatchAsync(batchHandle, cancellationToken))
				return false;

			_ = await PrimaryStorage.TryTriggerBatchAsync(batchHandle, cancellationToken);
			return true;
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerUpdatePayloadAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerUpdatePayloadAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage UpdatePayloadAsync called (JobHandle={JobHandle})"
	)]
	private partial void SingleServerUpdatePayloadAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerTryTriggerAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerTryTriggerAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage TryTriggerAsync called (JobHandle={JobHandle})"
	)]
	private partial void SingleServerTryTriggerAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerTryTriggerBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerTryTriggerBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage TryTriggerBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void SingleServerTryTriggerBatchAsyncCalled(BatchHandle batchHandle);
}

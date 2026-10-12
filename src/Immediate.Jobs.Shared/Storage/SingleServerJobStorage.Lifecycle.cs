using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerCompleteAsyncCalled(jobHandle, executionNumber, workerId);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await DurableStorage.CompleteAsync(jobHandle, executionNumber, workerId, cancellationToken);
			await PrimaryStorage.CompleteAsync(jobHandle, executionNumber, workerId, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask CompleteWithContinuationsAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerCompleteWithContinuationsAsyncCalled(jobHandle, executionNumber, workerId, additions.Count);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await JobGraphStorage
				.CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, additions, cancellationToken);

			await PrimaryStorage
				.CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, additions, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
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
		SingleServerFailAsyncCalled(jobHandle, executionNumber, workerId, nextRetryAt);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await DurableStorage.FailAsync(jobHandle, executionNumber, workerId, error, nextRetryAt, cancellationToken);
			await PrimaryStorage.FailAsync(jobHandle, executionNumber, workerId, error, nextRetryAt, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		SingleServerCancelBatchAsyncCalled(batchHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await JobGraphStorage.CancelBatchAsync(batchHandle, cancellationToken);
			await PrimaryStorage.CancelBatchAsync(batchHandle, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		SingleServerDeleteBatchAsyncCalled(batchHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await JobGraphStorage.DeleteBatchAsync(batchHandle, cancellationToken);
			await PrimaryStorage.DeleteBatchAsync(batchHandle, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		SingleServerCancelAsyncCalled(jobHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await DurableStorage.CancelAsync(jobHandle, cancellationToken);
			await PrimaryStorage.CancelAsync(jobHandle, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		SingleServerRetryAsyncCalled(jobHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await DurableStorage.RetryAsync(jobHandle, cancellationToken);
			await PrimaryStorage.RetryAsync(jobHandle, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		SingleServerDeleteAsyncCalled(jobHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await DurableStorage.DeleteAsync(jobHandle, cancellationToken);
			await PrimaryStorage.DeleteAsync(jobHandle, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerPurgeJobsAsyncCalled(succeededRetention, failedRetention);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await DurableStorage
				.PurgeJobsAsync(
					succeededRetention,
					failedRetention,
					cancellationToken
				);

			await PrimaryStorage
				.PurgeJobsAsync(
					succeededRetention,
					failedRetention,
					cancellationToken
				);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerPurgeBatchesAsyncCalled(batchSucceededRetention, batchFailedRetention);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await JobGraphStorage
				.PurgeBatchesAsync(
					batchSucceededRetention,
					batchFailedRetention,
					cancellationToken
				);

			await PrimaryStorage
				.PurgeBatchesAsync(
					batchSucceededRetention,
					batchFailedRetention,
					cancellationToken
				);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerCompleteAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerCompleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage CompleteAsync called (JobHandle={JobHandle}, ExecutionNumber={ExecutionNumber}, WorkerId={WorkerId})"
	)]
	private partial void SingleServerCompleteAsyncCalled(JobHandle jobHandle, int executionNumber, string workerId);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerCompleteWithContinuationsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerCompleteWithContinuationsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage CompleteWithContinuationsAsync called (JobHandle={JobHandle}, ExecutionNumber={ExecutionNumber}, WorkerId={WorkerId}, Additions={Additions})"
	)]
	private partial void SingleServerCompleteWithContinuationsAsyncCalled(JobHandle jobHandle, int executionNumber, string workerId, int additions);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerFailAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerFailAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage FailAsync called (JobHandle={JobHandle}, ExecutionNumber={ExecutionNumber}, WorkerId={WorkerId}, NextRetryAt={NextRetryAt})"
	)]
	private partial void SingleServerFailAsyncCalled(JobHandle jobHandle, int executionNumber, string workerId, DateTimeOffset? nextRetryAt);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerCancelBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerCancelBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage CancelBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void SingleServerCancelBatchAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerDeleteBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerDeleteBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage DeleteBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void SingleServerDeleteBatchAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerCancelAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerCancelAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage CancelAsync called (JobHandle={JobHandle})"
	)]
	private partial void SingleServerCancelAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerRetryAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerRetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage RetryAsync called (JobHandle={JobHandle})"
	)]
	private partial void SingleServerRetryAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerDeleteAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerDeleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage DeleteAsync called (JobHandle={JobHandle})"
	)]
	private partial void SingleServerDeleteAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerPurgeJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerPurgeJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage PurgeJobsAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void SingleServerPurgeJobsAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerPurgeBatchesAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerPurgeBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage PurgeBatchesAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void SingleServerPurgeBatchesAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);
}

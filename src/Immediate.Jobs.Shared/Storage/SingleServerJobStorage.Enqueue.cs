using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		SingleServerEnqueueAsyncCalled(job.JobHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await DurableStorage.EnqueueAsync(job, cancellationToken);
			await PrimaryStorage.EnqueueAsync(job, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerEnqueueContinuationAsyncCalled(job.JobHandle, edges.Count);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await JobGraphStorage.EnqueueContinuationAsync(job, edges, cancellationToken);
			await PrimaryStorage.EnqueueContinuationAsync(job, edges, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerEnqueueBatchAsyncCalled(batch.BatchHandle, jobs.Count, edges.Count);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await JobGraphStorage.EnqueueBatchAsync(batch, jobs, edges, cancellationToken);
			await PrimaryStorage.EnqueueBatchAsync(batch, jobs, edges, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask AddBatchJobAsync(
		JobHandle currentJobHandle,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerAddBatchJobAsyncCalled(currentJobHandle, executionNumber, job.JobHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await JobGraphStorage.AddBatchJobAsync(currentJobHandle, executionNumber, job, options, cancellationToken);
			await PrimaryStorage.AddBatchJobAsync(currentJobHandle, executionNumber, job, options, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerEnqueueAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerEnqueueAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage EnqueueAsync called (JobHandle={JobHandle})"
	)]
	private partial void SingleServerEnqueueAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerEnqueueContinuationAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerEnqueueContinuationAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage EnqueueContinuationAsync called (JobHandle={JobHandle}, Edges={Edges})"
	)]
	private partial void SingleServerEnqueueContinuationAsyncCalled(JobHandle jobHandle, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerEnqueueBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerEnqueueBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage EnqueueBatchAsync called (BatchHandle={BatchHandle}, Jobs={Jobs}, Edges={Edges})"
	)]
	private partial void SingleServerEnqueueBatchAsyncCalled(BatchHandle batchHandle, int jobs, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerAddBatchJobAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerAddBatchJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage AddBatchJobAsync called (CurrentJobHandle={CurrentJobHandle}, ExecutionNumber={ExecutionNumber}, JobHandle={JobHandle})"
	)]
	private partial void SingleServerAddBatchJobAsyncCalled(JobHandle currentJobHandle, int executionNumber, JobHandle jobHandle);
}

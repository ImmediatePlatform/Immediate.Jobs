using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerAcquireDueJobsAsyncCalled(request.WorkerId, request.BatchSize, request.Queues.Count);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			// Definition pause, counters, and acquisition history must survive process restart.
			// Reserve them with the durable claim, then mirror the committed ownership into the primary.
			var acquired = await DurableStorage
				.AcquireDueJobsAsync(request, cancellationToken);

			if (acquired.Count == 0)
				return acquired;

			PrimaryStorage.ApplyAcquiredJobs(acquired);

			return acquired;
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerRenewLeaseAsyncCalled(jobHandle, executionNumber, workerId, lease);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);

		try
		{
			await DurableStorage.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);
			await PrimaryStorage.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerAcquireDueJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerAcquireDueJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage AcquireDueJobsAsync called (WorkerId={WorkerId}, BatchSize={BatchSize}, Queues={Queues})"
	)]
	private partial void SingleServerAcquireDueJobsAsyncCalled(string workerId, int batchSize, int queues);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerRenewLeaseAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerRenewLeaseAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage RenewLeaseAsync called (JobHandle={JobHandle}, ExecutionNumber={ExecutionNumber}, WorkerId={WorkerId}, Lease={Lease})"
	)]
	private partial void SingleServerRenewLeaseAsyncCalled(JobHandle jobHandle, int executionNumber, string workerId, TimeSpan lease);
}

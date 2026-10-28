using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	// Recheck durable limits before the next selection if recovery or a boundary caused a partial claim.
	// Refresh before making new claims so a failed refresh cannot hide an already committed acquisition.
	private readonly HashSet<string> _acquisitionDefinitionsToRefresh = [with(StringComparer.OrdinalIgnoreCase)];

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
			foreach (var name in _acquisitionDefinitionsToRefresh.ToList())
			{
				if (!request.Queues.Any(queue => queue.JobCapacities.ContainsKey(name)))
					continue;

				var state = await DurableStorage.GetJobAcquisitionStateAsync(name, cancellationToken);
				PrimaryStorage.ApplyDefinitionAcquisitionStates([state]);
				_acquisitionDefinitionsToRefresh.Remove(name);
			}

			var selected = await PrimaryStorage.ProvisionallyAcquireDueJobsAsync(request, cancellationToken);
			if (selected.Count == 0)
				return [];

			var acquired = await JobGraphStorage.AcquireJobsAsync(selected, request.WorkerId, request.Lease, cancellationToken);
			// Ownership must reach the mirror even if cancellation arrives after the durable commit.
			var mirrored = await PrimaryStorage.AcquireJobsAsync(
				acquired.Select(static job => job.JobHandle).ToList(), request.WorkerId, request.Lease, CancellationToken.None);
			if (mirrored.Count != acquired.Count || mirrored.Where((job, index) =>
				job.JobHandle != acquired[index].JobHandle || job.Attempt != acquired[index].Attempt).Any())
			{
				throw new ImmediateJobException("The single-server primary has drifted from durable storage. Use only one scheduler process.");
			}

			PrimaryStorage.ApplyFairQueueAcquisitions(mirrored, request.FairQueues);

			if (acquired.Count != selected.Count)
			{
				var confirmed = acquired.Select(static job => job.JobHandle).ToHashSet();
				foreach (var handle in selected.Where(handle => !confirmed.Contains(handle)))
				{
					var job = await PrimaryStorage.GetJobStatusAsync(handle, CancellationToken.None);
					_acquisitionDefinitionsToRefresh.Add(job!.JobName);
				}
			}

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
			PrimaryStorage.ApplyRenewedLease(jobHandle, executionNumber, workerId, lease);
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

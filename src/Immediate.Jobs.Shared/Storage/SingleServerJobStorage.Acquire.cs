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

		var acquired = await PrimaryStorage
			.AcquireDueJobsAsync(request, cancellationToken);

		if (acquired.Count == 0)
			return acquired;

		var replicated = await JobGraphStorage
			.AcquireJobsAsync(
				[.. acquired.Select(x => x.JobHandle)],
				request.WorkerId,
				request.Lease,
				cancellationToken
			);

		var replicatedExecutions = replicated.ToDictionary(static job => job.JobHandle, static job => job.Attempt);

		if (acquired.Count != replicated.Count ||
			acquired.Any(job => !replicatedExecutions.TryGetValue(job.JobHandle, out var attempt) || attempt != job.Attempt))
		{
			throw new ImmediateJobException(
				"The durable job replica has drifted from the authoritative in-memory queue. " +
				"Single-server mode must not be used by multiple scheduler processes."
			);
		}

		return acquired;
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

		await DurableStorage.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);
		await PrimaryStorage.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);
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

using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		AcquireDueJobsAsyncCalled(request.WorkerId, request.BatchSize, request.Queues.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var keys = new List<RedisKey>(4 + request.Queues.Count)
		{
			LeasesKey,
			StateKey(JobState.Active),
			StateKey(JobState.Pending),
			StateKey(JobState.Scheduled),
		};
		var now = _timeProvider.GetUtcNow();
		var leaseExpiresAt = now + request.Lease;
		var values = new List<RedisValue>
		{
			Score(now),
			Score(leaseExpiresAt),
			Ticks(leaseExpiresAt),
			request.WorkerId,
			request.BatchSize,
			request.Queues.Count,
			_root,
			Ticks(now),
			request.FairQueues is null ? 0 : 1,
			request.FairQueues?.ConcurrencyShareThreshold ?? 0,
			request.FairQueues?.MinInflightForNoisy ?? 0,
			request.FairQueues?.GroupRoundRobin == true ? 1 : 0,
		};
		foreach (var queue in request.Queues)
		{
			keys.Add(DueKey(queue.QueueName));
			values.Add(queue.QueueName);
			values.Add(Math.Max(0, queue.Capacity));
			values.Add(queue.JobCapacities.Count);
			foreach (var capacity in queue.JobCapacities)
			{
				values.Add(capacity.Key.ToUpperInvariant());
				values.Add(Math.Max(0, capacity.Value));
			}
		}

		var result = await Database.ScriptEvaluateAsync(
			RedisScripts.Acquire,
			[.. keys],
			[.. values]
		).WaitAsync(cancellationToken);
		var ids = ((RedisResult[])result!)
			.Select(static value => (string)value!)
			.ToList();
		return await ReadJobsAsync(ids, cancellationToken);
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
		RenewLeaseAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var expiresAt = _timeProvider.GetUtcNow() + lease;
		var result = await EvaluateInt64Async(
			RedisScripts.RenewLease,
			[JobKey(jobHandle), LeasesKey],
			[workerId, executionNumber, Ticks(expiresAt), Score(expiresAt), jobHandle.Value],
			cancellationToken
		);
		ThrowIfNotOwned(result, jobHandle, workerId);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.AcquireDueJobsAsyncCalled,
		EventName = "Immediate.Jobs.Redis.AcquireDueJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AcquireDueJobsAsync called (Worker={Worker}, BatchSize={BatchSize}, Queues={Queues})"
	)]
	private partial void AcquireDueJobsAsyncCalled(string worker, int batchSize, int queues);

	[LoggerMessage(
		EventId = LibraryEventIds.RenewLeaseAsyncCalled,
		EventName = "Immediate.Jobs.Redis.RenewLeaseAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RenewLeaseAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void RenewLeaseAsyncCalled(JobHandle jobHandle, int execution);
}

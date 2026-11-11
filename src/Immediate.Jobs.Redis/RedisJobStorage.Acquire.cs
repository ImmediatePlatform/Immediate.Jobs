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

		RedisKey[] keys =
		[
			LeasesKey,
			StateKey(JobState.Active),
			StateKey(JobState.Pending),
			StateKey(JobState.Scheduled),
			DefinitionMetadataKey,
		];
		var names = request.Queues.SelectMany(static queue => queue.JobCapacities.Keys)
			.Select(static name => name.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
		if (names.Count == 0)
			return [];

		var refresh = false;
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var definitions = await ReadDefinitionSnapshotsAsync(names, refresh, cancellationToken);

			var now = _timeProvider.GetUtcNow();
			var leaseExpiresAt = now + request.Lease;
			var values = new List<RedisValue>
			{
				Score(now),
				Score(leaseExpiresAt),
				Timestamp(leaseExpiresAt),
				request.WorkerId,
				request.BatchSize,
				request.Queues.Count,
				_root,
				Timestamp(now),
				request.FairQueues is null ? 0 : 1,
				request.FairQueues?.ConcurrencyShareThreshold ?? 0,
				request.FairQueues?.MinInflightForNoisy ?? 0,
				request.FairQueues?.GroupRoundRobin == true ? 1 : 0,
			};
			foreach (var queue in request.Queues)
			{
				values.Add(queue.QueueName);
				values.Add(Math.Max(0, queue.Capacity));
				var countIndex = values.Count;
				values.Add(0);
				var count = 0;
				foreach (var capacity in queue.JobCapacities)
				{
					var name = capacity.Key.ToUpperInvariant();
					if (!definitions.TryGetValue(name, out var snapshot))
						continue;
					count++;
					values.Add(name);
					values.Add(Math.Max(0, capacity.Value));
					values.Add(snapshot.Metadata);
					AddDefinitionLimits(values, snapshot.Limits, now);
				}

				values[countIndex] = count;
			}

			var result = await Database.ScriptEvaluateAsync(RedisScripts.Acquire, keys, [.. values]).WaitAsync(cancellationToken);
			// Retry if a definition changed after its limits were read.
			if (result.IsNull)
			{
				refresh = true;
				continue;
			}

			return ReadJobRecords(result);
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
		RenewLeaseAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var expiresAt = _timeProvider.GetUtcNow() + lease;
		var result = await EvaluateInt64Async(
			RedisScripts.RenewLease,
			[JobKey(jobHandle), LeasesKey],
			[workerId, executionNumber, Timestamp(expiresAt), Score(expiresAt), jobHandle.Value, _root],
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

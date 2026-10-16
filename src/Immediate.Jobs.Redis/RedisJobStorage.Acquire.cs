using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
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

		RedisValue[] definitionKeys = [.. names.Select(static name => (RedisValue)name)];
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var metadata = await Database.HashGetAsync(DefinitionMetadataKey, definitionKeys).WaitAsync(cancellationToken);
			var definitions = new Dictionary<string, (RedisValue Metadata, JobDefinition Definition)>(StringComparer.Ordinal);
			for (var index = 0; index < names.Count; index++)
			{
				if (metadata[index].IsNull)
					continue;

				var definition = RedisJsonSerializerContext.DeserializeJobDefinition(metadata[index].ToString())
					?? throw new ImmediateJobException($"Job definition '{names[index]}' contains invalid data.");
				definitions.Add(names[index], (metadata[index], definition));
			}

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
				var capacities = queue.JobCapacities.Where(pair => definitions.ContainsKey(pair.Key.ToUpperInvariant())).ToList();
				values.Add(queue.QueueName);
				values.Add(Math.Max(0, queue.Capacity));
				values.Add(capacities.Count);
				foreach (var capacity in capacities)
				{
					var name = capacity.Key.ToUpperInvariant();
					var (snapshot, definition) = definitions[name];
					values.Add(name);
					values.Add(Math.Max(0, capacity.Value));
					values.Add(snapshot);
					AddDefinitionLimits(values, definition.AcquisitionLimits, now);
				}
			}

			var result = await Database.ScriptEvaluateAsync(RedisScripts.Acquire, keys, [.. values]).WaitAsync(cancellationToken);
			// Retry if a definition changed after its limits were read.
			if ((RedisResult[]?)result is not { } valuesReturned)
				continue;

			var ids = new List<string>(valuesReturned.Length / 2);
			var queues = new Dictionary<string, string>(StringComparer.Ordinal);
			for (var index = 0; index < valuesReturned.Length; index += 2)
			{
				var id = (string)valuesReturned[index]!;
				ids.Add(id);
				queues[id] = (string)valuesReturned[index + 1]!;
			}

			var jobs = await ReadJobsDataAsync(ids, cancellationToken);
			return [.. jobs.Select(job => job with { QueueName = queues[job.JobHandle.Value] })];
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

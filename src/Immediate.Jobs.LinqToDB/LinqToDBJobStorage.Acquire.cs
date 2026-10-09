using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
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

		foreach (var limits in request.JobLimits.Values)
			limits.Validate();

		if (request.FairQueues is not null)
			return await AcquireDueJobsFairAsync(request, cancellationToken);

		var now = timeProvider.GetUtcNow();
		var acquired = new List<JobRecord>(request.BatchSize);
		foreach (var queue in request.Queues)
		{
			var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
			if (queueCapacity <= 0)
				continue;

			var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
			while (queueCapacity > 0)
			{
				var eligibleNames = await FilterAcquisitionNamesAsync(jobCapacities, request, cancellationToken);
				if (eligibleNames.Count == 0)
					break;

				await using var scope = contextScope.GetScope(out var readConnection);

				var candidates = await Jobs(readConnection)
					.Where(job => job.QueueName == queue.QueueName && eligibleNames.Contains(job.JobName) &&
						(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
							|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
					.OrderBy(job => job.DueAt)
					.ThenBy(job => job.CreatedAt)
					.ThenBy(job => job.Id)
					.Take(queueCapacity)
					.ToListAsync(cancellationToken);
				if (candidates.Count == 0)
					break;

				var selectionCapacities = new Dictionary<string, int>(jobCapacities, StringComparer.OrdinalIgnoreCase);
				var selected = candidates.Where(candidate => selectionCapacities[candidate.JobName]-- > 0).ToList();
				var claimed = await AcquireCandidatesAsync(selected, request.WorkerId, request.Lease, now, cancellationToken,
				request);
				foreach (var job in claimed)
				{
					jobCapacities[job.JobName]--;
					queueCapacity--;
					acquired.Add(job);
				}

				if (claimed.Count == 0)
					break;
			}
		}

		return acquired;
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<JobHandle> jobHandles,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		AcquireJobsAsyncCalled(workerId, jobHandles.Count, lease);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		if (jobHandles.Count == 0)
			return [];

		var now = timeProvider.GetUtcNow();

		await using var scope = contextScope.GetScope(out var connection);

		var candidates = await Jobs(connection)
			.Where(job => job.Id.In(jobHandles.Select(static job => job.Value)) &&
				(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
					|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
			.ToListAsync(cancellationToken);

		return await AcquireCandidatesAsync(candidates, workerId, lease, now, cancellationToken);
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

		await using var scope = contextScope.GetScope(out var connection);

		var updated = await Jobs(connection)
			.Where(job => job.Id == jobHandle.Value && job.Attempt == executionNumber && job.State == JobState.Active && job.WorkerId == workerId)
			.Set(job => job.LeaseExpiresAt, timeProvider.GetUtcNow() + lease)
			.Set(job => job.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken);
		if (updated == 0)
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
	}

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireCandidatesAsync(
		List<ImmediateJobEntity> candidates,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		CancellationToken cancellationToken,
		JobAcquisitionRequest? request = null
	)
	{
		var acquired = new List<JobRecord>(candidates.Count);
		foreach (var candidate in candidates)
		{
			await EnsureDefinitionAsync(candidate.JobName, cancellationToken);
			await using var scope = contextScope.GetScope(out var connection);

			_ = await connection.BeginTransactionAsync(cancellationToken);
			try
			{
				if (!await ReserveDefinitionAsync(connection, candidate.JobName, request, now, cancellationToken))
				{
					await connection.CommitTransactionAsync(cancellationToken);
					continue;
				}

				var previous = ToRecord(candidate);
				var oldStamp = candidate.ConcurrencyStamp;
				candidate.State = JobState.Active;
				candidate.WorkerId = workerId;
				candidate.LeaseExpiresAt = now + lease;
				candidate.Attempt++;
				candidate.CompletedAt = null;
				candidate.ExecutionTraceId = null;
				candidate.ExecutionSpanId = null;
				candidate.ExecutionStartedAt = null;
				candidate.ConcurrencyStamp = Guid.NewGuid();
				if (!await UpdateJobAsync(connection, candidate, oldStamp, cancellationToken))
				{
					await connection.RollbackTransactionAsync(cancellationToken);
					continue;
				}

				await PrepareAcquisitionExecutionsAsync(connection, previous, workerId, now, cancellationToken);

				if (candidate.BatchHandle is { } batchHandle)
				{
					var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle, cancellationToken);
					if (batch is not null && batch.StartedAt is null)
					{
						var batchStamp = batch.ConcurrencyStamp;
						batch.StartedAt = now;
						batch.ConcurrencyStamp = Guid.NewGuid();
						if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken))
							throw new LostRaceException();
					}
				}

				await RefreshAcquiredDefinitionAsync(connection, candidate.JobName, request, now, cancellationToken);
				await connection.CommitTransactionAsync(cancellationToken);
				acquired.Add(ToRecord(candidate));
			}

			catch (SyntheticExecutionInsertFailedException exception)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				if (!await SyntheticExecutionExistsAsync(exception.JobHandle, exception.Attempt, cancellationToken))
				{
					throw exception.DatabaseException;
				}
			}

			catch (LostRaceException)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
			}
		}

		return acquired;
	}

	[LoggerMessage(
		EventId = LibraryEventIds.AcquireDueJobsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.AcquireDueJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AcquireDueJobsAsync called (Worker={Worker}, BatchSize={BatchSize}, Queues={Queues})"
	)]
	private partial void AcquireDueJobsAsyncCalled(string worker, int batchSize, int queues);

	[LoggerMessage(
		EventId = LibraryEventIds.AcquireJobsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.AcquireJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AcquireJobsAsync called (Worker={Worker}, Jobs={Jobs}, Lease={Lease})"
	)]
	private partial void AcquireJobsAsyncCalled(string worker, int jobs, TimeSpan lease);

	[LoggerMessage(
		EventId = LibraryEventIds.RenewLeaseAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.RenewLeaseAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RenewLeaseAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void RenewLeaseAsyncCalled(JobHandle jobHandle, int execution);
}

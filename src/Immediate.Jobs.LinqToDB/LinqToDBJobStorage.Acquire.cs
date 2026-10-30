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

		if (request.FairQueues is not null)
			return await AcquireDueJobsFairAsync(request, cancellationToken);

		var now = timeProvider.GetUtcNow();
		var acquired = new List<JobRecord>(request.BatchSize);
		foreach (var queue in request.Queues)
		{
			var capacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
			if (capacity <= 0)
				continue;
			var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
			acquired.AddRange(await AcquireQueueInExistingOrderAsync(queue.QueueName, jobCapacities, capacity,
				request.WorkerId, request.Lease, now, cancellationToken));
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

		var candidates = await JobsWithDefinitions(connection)
			.Where(job => job.Id.In(jobHandles.Select(static job => job.Value)) &&
				(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
					|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
			.ToListAsync(cancellationToken);

		// Preserve the in-memory scheduler's priority when limits permit only part of the requested set.
		var order = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var handle in jobHandles)
			order.TryAdd(handle.Value, order.Count);
		candidates.Sort((left, right) => order[left.Id].CompareTo(order[right.Id]));

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

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireQueueInExistingOrderAsync(
		string queueName,
		Dictionary<string, int> jobCapacities,
		int queueCapacity,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		CancellationToken cancellationToken,
		List<string>? initialEligibleNames = null
	)
	{
		var acquired = new List<JobRecord>(queueCapacity);
		while (queueCapacity > 0)
		{
			var eligibleNames = initialEligibleNames ?? await FilterAcquisitionNamesAsync(jobCapacities, cancellationToken);
			initialEligibleNames = null;
			if (eligibleNames.Count == 0)
				break;

			await using var scope = contextScope.GetScope(out var readConnection);

			var candidates = await JobsWithDefinitions(readConnection)
				.Where(job => job.QueueName == queueName && eligibleNames.Contains(job.JobName) &&
					(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
						|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
				.OrderBy(job => job.DueAt)
				.ThenBy(job => job.CreatedAt)
				.ThenBy(job => job.Id)
				.Take(queueCapacity)
				.ToListAsync(cancellationToken);
			if (candidates.Count == 0)
				break;

			var selected = new List<JobQueryRow>(candidates.Count);
			var selectionCapacities = new Dictionary<string, int>(jobCapacities, StringComparer.OrdinalIgnoreCase);
			foreach (var candidate in candidates)
			{
				if (selectionCapacities[candidate.JobName] <= 0)
					continue;
				selectionCapacities[candidate.JobName]--;
				selected.Add(candidate);
			}

			var claimed = await AcquireCandidatesAsync(
				selected,
				workerId,
				lease,
				now,
				cancellationToken
			);
			foreach (var job in claimed)
			{
				jobCapacities[job.JobName]--;
				queueCapacity--;
				acquired.Add(job);
			}

			if (claimed.Count == 0)
				break;
		}

		return acquired;
	}

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireCandidatesAsync(
		List<JobQueryRow> candidates,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var acquired = new List<JobRecord>(candidates.Count);
		await EnsureDefinitionsAsync(candidates.Select(static candidate => candidate.JobName), cancellationToken);
		var fallbackUntil = 0;
		for (var index = 0; index < candidates.Count; index++)
		{
			var candidate = candidates[index];
			var end = index;
			while (end < candidates.Count && end - index < 256 && candidates[end].Attempt == 0
				&& candidates[end].BatchHandle == null && string.Equals(candidates[end].JobName, candidate.JobName, StringComparison.Ordinal))
				end++;
			if (index >= fallbackUntil && end - index >= 3)
			{
				var claimed = await TryAcquireFreshJobsAsync(candidates.GetRange(index, end - index), workerId, lease, now, cancellationToken);
				if (claimed is not null)
				{
					acquired.AddRange(claimed);
					index = end - 1;
					continue;
				}

				fallbackUntil = end;
			}

			await using var scope = contextScope.GetScope(out var connection);

			await connection.BeginTransactionAsync(cancellationToken);
			try
			{
				if (await ReserveDefinitionAsync(connection, candidate.JobName, now, cancellationToken) is not { } reservation)
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
					await Batches(connection).Where(batch => batch.Id == batchHandle && batch.StartedAt == null)
						.Set(batch => batch.StartedAt, now)
						.Set(batch => batch.ConcurrencyStamp, Guid.NewGuid())
						.UpdateAsync(cancellationToken);
				}

				await RefreshAcquiredDefinitionAsync(connection, reservation.Definition, reservation.Limits, now, cancellationToken);
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

	private async Task<IReadOnlyList<JobRecord>?> TryAcquireFreshJobsAsync(
		List<JobQueryRow> candidates, string workerId, TimeSpan lease, DateTimeOffset now, CancellationToken cancellationToken
	)
	{
		await using var scope = contextScope.GetScope(out var connection);
		var name = candidates[0].JobName;
		if (!await Definitions(connection).AnyAsync(item => item.Name == name && item.MaxConcurrency == 0
			&& item.SlidingWindowMax == 0 && item.FixedWindowMax == 0, cancellationToken))
			return null;

		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
		var definition = await LockDefinitionAsync(connection, name, cancellationToken);
		// Recheck metadata after taking the same lock used by ordinary claims and pause.
		var metadata = await Definitions(connection).SingleOrDefaultAsync(item => item.Name == name, cancellationToken);
		if (metadata is null || metadata.MaxConcurrency != 0 || metadata.SlidingWindowMax != 0 || metadata.FixedWindowMax != 0)
			return null;
		definition.AcquisitionStatus = definition.IsPaused ? JobAcquisitionStatus.Paused : JobAcquisitionStatus.Ready;
		definition.NextEligibleAt = null;
		if (definition.IsPaused)
		{
			await SaveDefinitionAsync(connection, definition, cancellationToken);
			await connection.CommitTransactionAsync(cancellationToken);
			return [];
		}

		var stamp = Guid.NewGuid();
		await VersionedJobs(connection, candidates)
			.Where(job => job.Attempt == 0 && job.BatchHandle == null
				&& (job.State == JobState.Pending || job.State == JobState.Scheduled) && job.DueAt <= now)
			.Set(job => job.State, JobState.Active).Set(job => job.Attempt, 1)
			.Set(job => job.WorkerId, workerId).Set(job => job.LeaseExpiresAt, now + lease)
			.Set(job => job.CompletedAt, (DateTimeOffset?)null)
			.Set(job => job.ExecutionTraceId, (string?)null).Set(job => job.ExecutionSpanId, (string?)null)
			.Set(job => job.ExecutionStartedAt, (DateTimeOffset?)null)
			.Set(job => job.ConcurrencyStamp, stamp).UpdateAsync(cancellationToken);
		var ids = candidates.Select(static job => job.Id).ToList();
		var claimed = (await Jobs(connection).Where(job => ids.Contains(job.Id) && job.ConcurrencyStamp == stamp)
			.Select(job => job.Id).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
		await InsertManyAsync(connection, claimed.Select(id => new ImmediateJobExecutionEntity
		{
			JobHandle = id,
			Attempt = 1,
			State = JobExecutionState.Active,
			WorkerId = workerId,
			AcquiredAt = now,
		}), cancellationToken);
		await SaveDefinitionAsync(connection, definition, cancellationToken);
		await connection.CommitTransactionAsync(cancellationToken);
		var acquired = new List<JobRecord>(claimed.Count);
		foreach (var candidate in candidates.Where(candidate => claimed.Contains(candidate.Id)))
		{
			candidate.State = JobState.Active;
			candidate.Attempt = 1;
			candidate.WorkerId = workerId;
			candidate.LeaseExpiresAt = now + lease;
			candidate.CompletedAt = null;
			candidate.ExecutionTraceId = null;
			candidate.ExecutionSpanId = null;
			candidate.ExecutionStartedAt = null;
			candidate.ConcurrencyStamp = stamp;
			acquired.Add(ToRecord(candidate));
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

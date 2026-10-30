using System.Linq.Expressions;
using System.Data.Common;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
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

		return request.FairQueues is null
			? await AcquireDueJobsInExistingOrderAsync(request, cancellationToken)
			: await AcquireDueJobsFairAsync(request, cancellationToken);
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

		var now = _timeProvider.GetUtcNow();
		var ids = jobHandles.Select(static job => job.Value).ToList();
		await using var readContext = await contextFactory.CreateDbContextAsync(cancellationToken);
		var candidates = await JobsWithDefinitions(readContext)
			.AsNoTracking()
			.Where(job => ids.Contains(job.Id) &&
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

		await ExecuteWithStrategyAsync(async token =>
		{
			await using var context = await contextFactory.CreateDbContextAsync(token);
			var expiresAt = _timeProvider.GetUtcNow() + lease;
			var stamp = Guid.NewGuid();
			var updated = await context.Set<ImmediateJobEntity>()
				.Where(job => job.Id == jobHandle.Value && job.Attempt == executionNumber &&
					job.State == JobState.Active && job.WorkerId == workerId)
				.ExecuteUpdateAsync(update => update
					.SetProperty(job => job.LeaseExpiresAt, expiresAt)
					.SetProperty(job => job.ConcurrencyStamp, stamp), token);
			if (updated == 0)
				throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
		}, cancellationToken);
	}

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsInExistingOrderAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken
	)
	{
		var now = _timeProvider.GetUtcNow();
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

			var candidates = await ReadWithStrategyAsync(
				(readContext, operationCancellationToken) => JobsWithDefinitions(readContext)
					.AsNoTracking()
					.Where(job => job.QueueName == queueName && eligibleNames.Contains(job.JobName) &&
						(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
							|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
					.OrderBy(job => job.DueAt)
					.ThenBy(job => job.CreatedAt)
					.ThenBy(job => job.Id)
					.Take(queueCapacity)
					.ToListAsync(operationCancellationToken),
				cancellationToken
			);
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
		var offset = 0;
		while (offset < candidates.Count)
		{
			var end = offset + 1;
			while (end < candidates.Count && end - offset < 256 &&
				string.Equals(candidates[end].JobName, candidates[offset].JobName, StringComparison.Ordinal))
				end++;
			var batch = candidates.GetRange(offset, end - offset);
			offset = end;
			if (batch.Count >= 3 && batch.All(static job => job.Attempt == 0 && job.BatchHandle == null))
			{
				await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
				var strategy = strategyContext.Database.CreateExecutionStrategy();
				var bulk = await strategy.ExecuteAsync(
					token => AcquireUnrestrictedCandidatesAsync(batch, workerId, lease, now, token), cancellationToken);
				if (bulk is not null)
				{
					acquired.AddRange(bulk);
					continue;
				}
			}

			foreach (var candidate in batch)
			{
				// Constrained, retried and batch-member claims retain their per-job transaction and priority.
				await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
				var strategy = strategyContext.Database.CreateExecutionStrategy();
				var record = await strategy.ExecuteAsync(
					token => AcquireCandidateCoreAsync(candidate, workerId, lease, now, token), cancellationToken);
				if (record is not null)
					acquired.Add(record);
			}
		}

		return acquired;
	}

	private async Task<IReadOnlyList<JobRecord>?> AcquireUnrestrictedCandidatesAsync(
		IReadOnlyList<JobQueryRow> candidates,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var name = candidates[0].JobName;
		// Avoid taking a write lock just to discover that this definition needs the ordinary claim path.
		if (!await context.Set<ImmediateJobDefinitionMetadataEntity>().AnyAsync(item => item.Name == name &&
			item.MaxConcurrency == 0 && item.SlidingWindowMax == 0 && item.FixedWindowMax == 0, cancellationToken))
			return null;

		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var definition = await LockDefinitionAsync(context, name, cancellationToken);
		// Re-read limits after acquiring the same definition lock used by pause and ordinary claims.
		var metadata = await context.Set<ImmediateJobDefinitionMetadataEntity>().AsNoTracking()
			.SingleOrDefaultAsync(item => item.Name == name, cancellationToken);
		if (metadata is null || metadata.MaxConcurrency != 0 || metadata.SlidingWindowMax != 0 || metadata.FixedWindowMax != 0)
			return null;
		definition.AcquisitionStatus = definition.IsPaused ? JobAcquisitionStatus.Paused : JobAcquisitionStatus.Ready;
		definition.NextEligibleAt = null;
		if (definition.IsPaused)
		{
			await context.SaveChangesAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return [];
		}

		var stamp = Guid.NewGuid();
		var expiresAt = now + lease;
		await context.Set<ImmediateJobEntity>().Where(MatchJobVersions(candidates))
			.Where(job => job.Attempt == 0 && job.BatchHandle == null &&
				(job.State == JobState.Pending || job.State == JobState.Scheduled) && job.DueAt <= now)
			.ExecuteUpdateAsync(update => update.SetProperty(job => job.State, JobState.Active)
				.SetProperty(job => job.Attempt, 1).SetProperty(job => job.WorkerId, workerId)
				.SetProperty(job => job.LeaseExpiresAt, expiresAt).SetProperty(job => job.CompletedAt, (DateTimeOffset?)null)
				.SetProperty(job => job.ExecutionTraceId, (string?)null).SetProperty(job => job.ExecutionSpanId, (string?)null)
				.SetProperty(job => job.ExecutionStartedAt, (DateTimeOffset?)null).SetProperty(job => job.ConcurrencyStamp, stamp), cancellationToken);
		var ids = candidates.Select(static job => job.Id).ToList();
		var claimed = (await context.Set<ImmediateJobEntity>().Where(job => ids.Contains(job.Id) && job.ConcurrencyStamp == stamp)
			.Select(job => job.Id).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
		var acquired = new List<JobRecord>(claimed.Count);
		foreach (var candidate in candidates.Where(candidate => claimed.Contains(candidate.Id)))
		{
			context.Add(new ImmediateJobExecutionEntity
			{
				JobHandle = candidate.Id,
				Attempt = 1,
				State = JobExecutionState.Active,
				WorkerId = workerId,
				AcquiredAt = now,
			});
			var entity = Copy(candidate);
			entity.State = JobState.Active;
			entity.Attempt = 1;
			entity.WorkerId = workerId;
			entity.LeaseExpiresAt = expiresAt;
			entity.CompletedAt = null;
			entity.ExecutionTraceId = null;
			entity.ExecutionSpanId = null;
			entity.ExecutionStartedAt = null;
			entity.ConcurrencyStamp = stamp;
			acquired.Add(ToRecord(entity, candidate.QueueName));
		}

		await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		return acquired;
	}

	private static Expression<Func<ImmediateJobEntity, bool>> MatchJobVersions(IEnumerable<ImmediateJobEntity> jobs)
	{
		var predicates = jobs.Select(job =>
		{
			Expression<Func<ImmediateJobEntity, bool>> matches = entity => entity.Id == job.Id && entity.ConcurrencyStamp == job.ConcurrencyStamp;
			return matches;
		}).ToList();
		if (predicates.Count == 0)
			return static _ => false;
		// Keep expression depth logarithmic for large recovery batches and SQLite's expression-depth limit.
		while (predicates.Count > 1)
		{
			var combined = new List<Expression<Func<ImmediateJobEntity, bool>>>();
			for (var index = 0; index < predicates.Count; index += 2)
			{
				var left = predicates[index];
				combined.Add(index + 1 == predicates.Count ? left : Expression.Lambda<Func<ImmediateJobEntity, bool>>(
					Expression.OrElse(left.Body, Expression.Invoke(predicates[index + 1], left.Parameters)), left.Parameters));
			}

			predicates = combined;
		}

		return predicates[0];
	}

	private async Task<JobRecord?> AcquireCandidateCoreAsync(
		JobQueryRow candidate,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		if (await ReserveDefinitionAsync(context, candidate.JobName, now, cancellationToken) is not { } limits)
		{
			await context.SaveChangesAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return null;
		}

		var entity = Copy(candidate);
		context.Attach(entity);
		await PrepareAcquisitionExecutionsAsync(context, candidate, workerId, now, cancellationToken);
		entity.State = JobState.Active;
		entity.WorkerId = workerId;
		entity.LeaseExpiresAt = now + lease;
		entity.Attempt++;
		entity.CompletedAt = null;
		entity.ExecutionTraceId = null;
		entity.ExecutionSpanId = null;
		entity.ExecutionStartedAt = null;
		entity.ConcurrencyStamp = Guid.NewGuid();
		if (entity.BatchHandle is { } batchHandle)
		{
			var batch = await context.Set<ImmediateJobBatchEntity>()
				.SingleOrDefaultAsync(item => item.Id == batchHandle, cancellationToken);
			if (batch is not null && batch.StartedAt is null)
			{
				batch.StartedAt = now;
				batch.ConcurrencyStamp = Guid.NewGuid();
			}
		}

		try
		{
			await context.SaveChangesAsync(cancellationToken);
			await RefreshAcquiredDefinitionAsync(context, candidate.JobName, limits, now, cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return ToRecord(entity, candidate.QueueName);
		}

		catch (DbUpdateException)
		{
			await transaction.RollbackAsync(cancellationToken);
			// Suppress only an expected optimistic-claim race; genuine provider failures remain visible.
			if (!await CandidateWasClaimedAsync(candidate, cancellationToken))
				throw;
		}

		return null;
	}

	private async ValueTask<bool> CandidateWasClaimedAsync(
		JobQueryRow candidate,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			var currentStamp = await JobsWithDefinitions(context)
				.AsNoTracking()
				.Where(job => job.Id == candidate.Id)
				.Select(static job => (Guid?)job.ConcurrencyStamp)
				.SingleOrDefaultAsync(cancellationToken);
			return currentStamp != candidate.ConcurrencyStamp;
		}

		catch (Exception exception) when (exception is DbException or InvalidOperationException)
		{
			return false;
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.AcquireDueJobsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.AcquireDueJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AcquireDueJobsAsync called (Worker={Worker}, BatchSize={BatchSize}, Queues={Queues})"
	)]
	private partial void AcquireDueJobsAsyncCalled(string worker, int batchSize, int queues);

	[LoggerMessage(
		EventId = LibraryEventIds.AcquireJobsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.AcquireJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AcquireJobsAsync called (Worker={Worker}, Jobs={Jobs}, Lease={Lease})"
	)]
	private partial void AcquireJobsAsyncCalled(string worker, int jobs, TimeSpan lease);

	[LoggerMessage(
		EventId = LibraryEventIds.RenewLeaseAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.RenewLeaseAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RenewLeaseAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void RenewLeaseAsyncCalled(JobHandle jobHandle, int execution);
}

using System.Data.Common;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;

// TODO: remove and fix diagnostics
#pragma warning disable MA0015 // Specify the parameter name in ArgumentException

namespace Immediate.Jobs.EntityFrameworkCore;

/// <summary>An optimistic-concurrency EF Core implementation of <see cref="IJobStorage"/>.</summary>
/// <typeparam name="TContext">The application context containing the Immediate.Jobs model.</typeparam>
/// <param name="contextFactory">The factory used to create application database contexts.</param>
/// <param name="timeProvider">The clock used for storage timestamps, or <see langword="null"/> to use the system clock.</param>
internal sealed class EntityFrameworkCoreJobStorage<TContext>(
	IDbContextFactory<TContext> contextFactory,
	TimeProvider? timeProvider = null
) : IRecurringJobStorage, IJobGraphStorage, IJobStorageReplica
	where TContext : DbContext
{
	private const int MaxConcurrencyAttempts = 5;
	private const int MaxConsecutiveFailedFairClaims = 5;
	private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

	/// <inheritdoc />
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		_ = context.Model.FindEntityType(typeof(ImmediateJobEntity))
			?? throw new ImmediateJobException("Immediate.Jobs entities are not configured. Call modelBuilder.AddImmediateJobs() from OnModelCreating.");
	}

	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(job);
		await ExecuteWithStrategyAsync(
			operationCancellationToken => EnqueueCoreAsync(job, operationCancellationToken),
			cancellationToken
		).ConfigureAwait(false);
	}

	private async Task EnqueueCoreAsync(JobRecord job, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		if (job.GroupId is { } groupId && !await HasLiveGroupJobsAsync(
			context,
			job.QueueName,
			groupId,
			cancellationToken
		).ConfigureAwait(false))
		{
			var cursor = await context.Set<ImmediateFairQueueGroupEntity>()
				.SingleOrDefaultAsync(
					group => group.QueueName == job.QueueName && group.GroupId == groupId,
					cancellationToken
				)
				.ConfigureAwait(false);
			if (cursor is not null)
				_ = context.Remove(cursor);
		}

		_ = context.Set<ImmediateJobEntity>().Add(ToEntity(job));
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(job);
		ArgumentNullException.ThrowIfNull(edges);
		await ExecuteGraphInsertAsync(batch: null, [job], edges, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(batch);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(edges);
		if (jobs.Count == 0)
			throw new ImmediateJobException("An atomic batch cannot be committed without jobs.");
		await ExecuteGraphInsertAsync(batch, jobs, edges, cancellationToken).ConfigureAwait(false);
	}

	private ValueTask ExecuteGraphInsertAsync(
		BatchRecord? batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken
	) => RetryConcurrencyAsync(
		operationCancellationToken => InsertGraphCoreAsync(batch, jobs, edges, operationCancellationToken),
		cancellationToken
	);

	private async Task InsertGraphCoreAsync(
		BatchRecord? batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken
	)
	{
		var jobIds = jobs.Select(static job => job.Id).ToHashSet(StringComparer.Ordinal);
		if (jobIds.Count != jobs.Count)
			throw new ImmediateJobException("A batch or continuation insert contains duplicate job identifiers.");
		if (batch is not null && jobs.Any(job => !string.Equals(job.BatchId, batch.Id, StringComparison.Ordinal)))
			throw new ImmediateJobException("Every atomic batch member must carry the committed batch identifier.");

		var edgeEntities = edges.Select(ToEntity).ToArray();
		if (edgeEntities.Any(edge => !jobIds.Contains(edge.ChildJobId)))
			throw new ImmediateJobException("Every continuation edge must target a job inserted by the same operation.");
		if (edgeEntities.DistinctBy(static edge => (edge.ChildJobId, edge.ParentKind, edge.ParentId)).Count() != edgeEntities.Length)
			throw new ImmediateJobException("Duplicate continuation edges are not allowed.");
		ThrowIfCyclic(jobIds, edgeEntities);

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var jobEntities = jobs.Select(ToEntity).ToDictionary(static job => job.Id, StringComparer.Ordinal);
		await EvaluateInitialDependenciesAsync(
			context,
			jobEntities,
			edgeEntities,
			_timeProvider.GetUtcNow(),
			cancellationToken
		).ConfigureAwait(false);

		if (batch is not null)
		{
			var terminal = jobEntities.Values.Where(static job => IsTerminal(job.State)).ToArray();
			var pending = jobEntities.Count - terminal.Length;
			var succeeded = terminal.Count(static job => job.State == JobState.Succeeded);
			var failed = terminal.Count(static job => job.State == JobState.Failed);
			var cancelled = terminal.Count(static job => job.State == JobState.Cancelled);
			var skipped = terminal.Count(static job => job.State == JobState.Skipped);
			_ = context.Add(new ImmediateJobBatchEntity
			{
				Id = batch.Id,
				CreatedAt = batch.CreatedAt,
				TotalJobs = jobEntities.Count,
				PendingCount = pending,
				SucceededCount = succeeded,
				FailedCount = failed,
				CancelledCount = cancelled,
				SkippedCount = skipped,
				StartedAt = batch.StartedAt,
				CompletedAt = pending == 0 ? batch.CompletedAt ?? _timeProvider.GetUtcNow() : null,
				State = pending == 0 ? GetTerminalBatchState(failed, cancelled) : BatchState.Executing,
				ConcurrencyStamp = Guid.NewGuid(),
			});
		}

		context.AddRange(jobEntities.Values);
		context.AddRange(edgeEntities);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Lease, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.BatchSize, 0);
		if (request.FairQueues is not null)
			return await AcquireDueJobsFairAsync(request, cancellationToken).ConfigureAwait(false);

		var now = _timeProvider.GetUtcNow();
		var acquired = new List<JobRecord>(request.BatchSize);
		foreach (var queue in request.Queues)
		{
			var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
			if (queueCapacity <= 0)
				continue;

			var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
			while (queueCapacity > 0)
			{
				var eligibleNames = jobCapacities.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToArray();
				if (eligibleNames.Length == 0)
					break;

				await using var readContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
				var candidates = await readContext.Set<ImmediateJobEntity>()
					.AsNoTracking()
					.Where(job => job.QueueName == queue.QueueName && eligibleNames.Contains(job.JobName) &&
						(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
							|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
					.OrderBy(job => job.DueAt)
					.ThenBy(job => job.CreatedAt)
					.ThenBy(job => job.Id)
					.Take(queueCapacity)
					.ToListAsync(cancellationToken)
					.ConfigureAwait(false);
				if (candidates.Count == 0)
					break;

				var selected = new List<ImmediateJobEntity>(candidates.Count);
				var selectionCapacities = new Dictionary<string, int>(jobCapacities, StringComparer.Ordinal);
				foreach (var candidate in candidates)
				{
					if (selectionCapacities[candidate.JobName] <= 0)
						continue;
					selectionCapacities[candidate.JobName]--;
					selected.Add(candidate);
				}

				var claimed = await AcquireCandidatesAsync(
					selected,
					request.WorkerId,
					request.Lease,
					now,
					cancellationToken
				).ConfigureAwait(false);
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

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsFairAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken
	)
	{
		var now = _timeProvider.GetUtcNow();
		var acquired = new List<JobRecord>(request.BatchSize);
		foreach (var queue in request.Queues)
		{
			var consecutiveFailedClaims = 0;
			var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
			if (queueCapacity <= 0)
				continue;

			var jobCapacities = queue.JobCapacities.ToDictionary(
				static pair => pair.Key,
				static pair => pair.Value,
				StringComparer.Ordinal
			);
			while (queueCapacity > 0)
			{
				var eligibleNames = jobCapacities
					.Where(static pair => pair.Value > 0)
					.Select(static pair => pair.Key)
					.ToArray();
				if (eligibleNames.Length == 0)
					break;

				await using var readContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
				var eligibleQuery = readContext.Set<ImmediateJobEntity>()
					.AsNoTracking()
					.Where(job => job.QueueName == queue.QueueName && eligibleNames.Contains(job.JobName) &&
						(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
							|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)));
				if (!await eligibleQuery
					.AnyAsync(static job => job.GroupId != null, cancellationToken)
					.ConfigureAwait(false))
				{
					var claimed = await AcquireFairFastPathAsync(
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						request.WorkerId,
						request.Lease,
						now,
						cancellationToken
					).ConfigureAwait(false);
					queueCapacity -= claimed.Count;
					acquired.AddRange(claimed);
					break;
				}

				var groupedHeads = await eligibleQuery
					.Where(static job => job.GroupId != null)
					.GroupBy(static job => job.GroupId)
					.Select(static group => group
						.OrderBy(job => job.DueAt)
						.ThenBy(job => job.CreatedAt)
						.ThenBy(job => job.Id)
						.First())
					.ToListAsync(cancellationToken)
					.ConfigureAwait(false);
				var ungroupedHead = await eligibleQuery
					.Where(static job => job.GroupId == null)
					.OrderBy(job => job.DueAt)
					.ThenBy(job => job.CreatedAt)
					.ThenBy(job => job.Id)
					.FirstOrDefaultAsync(cancellationToken)
					.ConfigureAwait(false);
				if (groupedHeads.Count == 0)
				{
					var claimed = await AcquireFairFastPathAsync(
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						request.WorkerId,
						request.Lease,
						now,
						cancellationToken
					).ConfigureAwait(false);
					queueCapacity -= claimed.Count;
					acquired.AddRange(claimed);
					break;
				}

				var activeQuery = readContext.Set<ImmediateJobEntity>()
					.AsNoTracking()
					.Where(job => job.QueueName == queue.QueueName
						&& job.State == JobState.Active
						&& job.LeaseExpiresAt > now);
				var totalInflight = await activeQuery
					.CountAsync(cancellationToken)
					.ConfigureAwait(false);
				var groupedHeadIds = groupedHeads.Select(static job => job.Id).ToArray();
				var cursorQuery = readContext.Set<ImmediateFairQueueGroupEntity>()
					.AsNoTracking()
					.Where(group => group.QueueName == queue.QueueName);
				var groupStateQuery = eligibleQuery
					.Where(job => groupedHeadIds.Contains(job.Id));
				var groupStates = request.FairQueues!.GroupRoundRobin
					? await groupStateQuery
						.Select(job => new FairQueueCandidateState(
							job.Id,
							activeQuery.Count(active => active.GroupId == job.GroupId),
							cursorQuery
								.Where(cursor => cursor.GroupId == job.GroupId)
								.Select(static cursor => cursor.LastServedSequence)
								.FirstOrDefault()
						))
						.ToDictionaryAsync(static state => state.JobId, StringComparer.Ordinal, cancellationToken)
						.ConfigureAwait(false)
					: await groupStateQuery
						.Select(job => new FairQueueCandidateState(
							job.Id,
							activeQuery.Count(active => active.GroupId == job.GroupId),
							0
						))
						.ToDictionaryAsync(static state => state.JobId, StringComparer.Ordinal, cancellationToken)
						.ConfigureAwait(false);
				var nextSequence = 0L;
				if (request.FairQueues.GroupRoundRobin)
				{
					var maxSequence = await cursorQuery
						.MaxAsync(static group => (long?)group.LastServedSequence, cancellationToken)
						.ConfigureAwait(false);
					nextSequence = (maxSequence ?? 0) + 1;
				}

				var candidates = ungroupedHead is null ? groupedHeads : [.. groupedHeads, ungroupedHead];
				var ranked = candidates.Select(job =>
				{
					FairQueueCandidateState? state = null;
					if (job.GroupId is not null)
						_ = groupStates.TryGetValue(job.Id, out state);
					var noisy = IsNoisy(job.GroupId, state?.Inflight ?? 0, totalInflight, request.FairQueues);
					return new
					{
						Job = job,
						Noisy = noisy,
						NoisyInflight = noisy ? state!.Inflight : 0,
						LastServedSequence = state?.LastServedSequence ?? 0,
					};
				});
				var selected = ranked
					.OrderBy(static candidate => candidate.Noisy)
					.ThenBy(static candidate => candidate.NoisyInflight)
					.ThenBy(candidate => request.FairQueues.GroupRoundRobin
						? candidate.LastServedSequence
						: 0)
					.ThenBy(static candidate => candidate.Job.DueAt)
					.ThenBy(static candidate => candidate.Job.CreatedAt)
					.ThenBy(static candidate => candidate.Job.Id, StringComparer.Ordinal)
					.First()
					.Job;
				var claimedJob = request.FairQueues.GroupRoundRobin
					? await AcquireFairCandidateAsync(
						selected,
						request.WorkerId,
						request.Lease,
						now,
						nextSequence,
						cancellationToken
					).ConfigureAwait(false)
					: GetFirstOrDefault(await AcquireCandidatesAsync(
							[selected],
							request.WorkerId,
							request.Lease,
							now,
							cancellationToken
						).ConfigureAwait(false));
				if (claimedJob is null)
				{
					if (++consecutiveFailedClaims >= MaxConsecutiveFailedFairClaims)
						break;
					continue;
				}

				consecutiveFailedClaims = 0;
				jobCapacities[claimedJob.JobName]--;
				queueCapacity--;
				acquired.Add(claimedJob);
			}
		}

		return acquired;
	}

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireFairFastPathAsync(
		string queueName,
		Dictionary<string, int> jobCapacities,
		int queueCapacity,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var acquired = new List<JobRecord>(queueCapacity);
		while (queueCapacity > 0)
		{
			var eligibleNames = jobCapacities
				.Where(static pair => pair.Value > 0)
				.Select(static pair => pair.Key)
				.ToArray();
			if (eligibleNames.Length == 0)
				break;

			await using var readContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
			var candidates = await readContext.Set<ImmediateJobEntity>()
				.AsNoTracking()
				.Where(job => job.QueueName == queueName && eligibleNames.Contains(job.JobName) &&
					(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
						|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
				.OrderBy(job => job.DueAt)
				.ThenBy(job => job.CreatedAt)
				.ThenBy(job => job.Id)
				.Take(queueCapacity)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			if (candidates.Count == 0)
				break;

			var selected = new List<ImmediateJobEntity>(candidates.Count);
			var selectionCapacities = new Dictionary<string, int>(jobCapacities, StringComparer.Ordinal);
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
			).ConfigureAwait(false);
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

	private async ValueTask<JobRecord?> AcquireFairCandidateAsync(
		ImmediateJobEntity candidate,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		long nextSequence,
		CancellationToken cancellationToken
	)
	{
		await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var strategy = strategyContext.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(
			operationCancellationToken => AcquireFairCandidateCoreAsync(
				candidate,
				workerId,
				lease,
				now,
				nextSequence,
				operationCancellationToken
			),
			cancellationToken
		).ConfigureAwait(false);
	}

	private async Task<JobRecord?> AcquireFairCandidateCoreAsync(
		ImmediateJobEntity candidate,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		long nextSequence,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var entity = Copy(candidate);
		_ = context.Attach(entity);
		await PrepareAcquisitionExecutionsAsync(context, candidate, workerId, now, cancellationToken).ConfigureAwait(false);
		entity.State = JobState.Active;
		entity.WorkerId = workerId;
		entity.LeaseExpiresAt = now + lease;
		entity.Attempt++;
		entity.CompletedAt = null;
		entity.ExecutionTraceId = null;
		entity.ExecutionSpanId = null;
		entity.ExecutionStartedAt = null;
		entity.ConcurrencyStamp = Guid.NewGuid();

		if (candidate.GroupId is { } groupId)
		{
			var group = await context.Set<ImmediateFairQueueGroupEntity>()
				.SingleOrDefaultAsync(
					item => item.QueueName == candidate.QueueName && item.GroupId == groupId,
					cancellationToken
				)
				.ConfigureAwait(false);
			if (group is null)
			{
				_ = context.Add(new ImmediateFairQueueGroupEntity
				{
					QueueName = candidate.QueueName,
					GroupId = groupId,
					LastServedSequence = nextSequence,
					ConcurrencyStamp = Guid.NewGuid(),
				});
			}
			else if (group.LastServedSequence >= nextSequence)
			{
				// Selection observed an older cursor snapshot. Re-rank instead of moving this group backward.
				return null;
			}
			else
			{
				group.LastServedSequence = nextSequence;
				group.ConcurrencyStamp = Guid.NewGuid();
			}
		}

		if (entity.BatchId is { } batchId)
		{
			var batch = await context.Set<ImmediateJobBatchEntity>()
				.SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
				.ConfigureAwait(false);
			if (batch is not null && batch.StartedAt is null)
			{
				batch.StartedAt = now;
				batch.ConcurrencyStamp = Guid.NewGuid();
			}
		}

		try
		{
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return ToRecord(entity);
		}
		catch (DbUpdateException)
		{
			// The job or its group cursor changed after candidate selection.
			return null;
		}
	}

	private static JobRecord? GetFirstOrDefault(IReadOnlyList<JobRecord> jobs) =>
		jobs.Count == 0 ? null : jobs[0];

	private static bool IsNoisy(
		string? groupId,
		int inflight,
		int totalInflight,
		FairQueuePolicy policy
	)
	{
		return groupId is not null
			&& totalInflight > 0
			&& inflight >= policy.MinInflightForNoisy
			&& (double)inflight / totalInflight > policy.ConcurrencyShareThreshold;
	}

	private sealed record FairQueueCandidateState(
		string JobId,
		int Inflight,
		long LastServedSequence
	);

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<string> jobIds,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(jobIds);
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
		if (jobIds.Count == 0)
			return [];

		var now = _timeProvider.GetUtcNow();
		var ids = jobIds.ToArray();
		await using var readContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var candidates = await readContext.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.Where(job => ids.Contains(job.Id) &&
				(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
				|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

		return await AcquireCandidatesAsync(candidates, workerId, lease, now, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireCandidatesAsync(
		List<ImmediateJobEntity> candidates,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var acquired = new List<JobRecord>(candidates.Count);
		foreach (var candidate in candidates)
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
			var entity = Copy(candidate);
			_ = context.Attach(entity);
			await PrepareAcquisitionExecutionsAsync(context, candidate, workerId, now, cancellationToken).ConfigureAwait(false);
			entity.State = JobState.Active;
			entity.WorkerId = workerId;
			entity.LeaseExpiresAt = now + lease;
			entity.Attempt++;
			entity.CompletedAt = null;
			entity.ExecutionTraceId = null;
			entity.ExecutionSpanId = null;
			entity.ExecutionStartedAt = null;
			entity.ConcurrencyStamp = Guid.NewGuid();
			if (entity.BatchId is { } batchId)
			{
				var batch = await context.Set<ImmediateJobBatchEntity>()
					.SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
					.ConfigureAwait(false);
				if (batch is not null && batch.StartedAt is null)
				{
					batch.StartedAt = now;
					batch.ConcurrencyStamp = Guid.NewGuid();
				}
			}

			try
			{
				_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
				acquired.Add(ToRecord(entity));
			}
			catch (DbUpdateException)
			{
				// Suppress only an expected optimistic-claim race; genuine provider failures remain visible.
				if (!await CandidateWasClaimedAsync(candidate, cancellationToken).ConfigureAwait(false))
					throw;
			}
		}

		return acquired;
	}

	private async ValueTask<bool> CandidateWasClaimedAsync(
		ImmediateJobEntity candidate,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
			var currentStamp = await context.Set<ImmediateJobEntity>()
				.AsNoTracking()
				.Where(job => job.Id == candidate.Id)
				.Select(static job => (Guid?)job.ConcurrencyStamp)
				.SingleOrDefaultAsync(cancellationToken)
				.ConfigureAwait(false);
			return currentStamp != candidate.ConcurrencyStamp;
		}
		catch (Exception exception) when (exception is DbException or InvalidOperationException)
		{
			return false;
		}
	}

	/// <inheritdoc />
	public ValueTask SetExecutionTelemetryAsync(
		string jobId,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	) => MutateOwnedAsync(jobId, executionNumber, workerId, (job, execution) =>
	{
		job.ExecutionTraceId = traceId;
		job.ExecutionSpanId = spanId;
		job.ExecutionStartedAt = startedAt;
		execution.ExecutionTraceId = traceId;
		execution.ExecutionSpanId = spanId;
		execution.ExecutionStartedAt = startedAt;
	}, cancellationToken);

	/// <inheritdoc />
	public ValueTask RenewLeaseAsync(
		string jobId,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
		return MutateOwnedAsync(
			jobId,
			executionNumber,
			workerId,
			(job, _) => job.LeaseExpiresAt = _timeProvider.GetUtcNow() + lease,
			cancellationToken
		);
	}

	/// <inheritdoc />
	public ValueTask CompleteAsync(
		string jobId,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	) => CompleteWithContinuationsAsync(jobId, executionNumber, workerId, [], cancellationToken);

	/// <inheritdoc />
	public ValueTask CompleteWithContinuationsAsync(
		string jobId,
		int executionNumber,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(additions);
		return MutateOwnedWithDependenciesAsync(
			jobId,
			executionNumber,
			workerId,
			error: null,
			nextRetryAt: null,
			succeeded: true,
			additions,
			cancellationToken
		);
	}

	/// <inheritdoc />
	public ValueTask AddBatchJobAsync(
		string currentJobId,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(job);
		if (options == ContinuationOptions.Detached)
			throw new ImmediateJobException("AddToBatchAsync cannot create a detached job.");
		return RetryConcurrencyAsync(
			operationCancellationToken => AddBatchJobCoreAsync(
				currentJobId,
				executionNumber,
				job,
				options,
				operationCancellationToken
			),
			cancellationToken
		);
	}

	/// <inheritdoc />
	public ValueTask FailAsync(
		string jobId,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	) => MutateOwnedWithDependenciesAsync(
		jobId,
		executionNumber,
		workerId,
		error,
		nextRetryAt,
		succeeded: false,
		[],
		cancellationToken
	);

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		if (await UpdateRecurringAsync(context, schedule, cancellationToken).ConfigureAwait(false) != 0)
			return;
		await ThrowIfReplacingCodeDefinedScheduleAsync(context, schedule, cancellationToken).ConfigureAwait(false);

		_ = context.Add(ToEntity(schedule));
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateException)
		{
			// A competing node inserted the same schedule after our update attempt.
			await using var retryContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
			if (await UpdateRecurringAsync(retryContext, schedule, cancellationToken).ConfigureAwait(false) != 0)
				return;
			await ThrowIfReplacingCodeDefinedScheduleAsync(retryContext, schedule, cancellationToken).ConfigureAwait(false);
			throw;
		}
	}

	/// <inheritdoc />
	public async ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(activeScheduleNames);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var schedules = context.Set<ImmediateRecurringJobEntity>()
			.Where(schedule => schedule.IsCodeDefined);
		if (activeScheduleNames.Count != 0)
			schedules = schedules.Where(schedule => !activeScheduleNames.Contains(schedule.Name));
		_ = await schedules.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var removed = await context.Set<ImmediateRecurringJobEntity>()
			.Where(schedule => schedule.Name == name && !schedule.IsCodeDefined)
			.ExecuteDeleteAsync(cancellationToken)
			.ConfigureAwait(false);
		if (removed != 0)
			return;
		if (await context.Set<ImmediateRecurringJobEntity>()
			.AnyAsync(schedule => schedule.Name == name, cancellationToken)
			.ConfigureAwait(false))
		{
			throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");
		}

		throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
	}

	/// <inheritdoc />
	public ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
		=> MutateRecurringAsync(name, schedule => schedule.IsPaused = true, cancellationToken);

	/// <inheritdoc />
	public ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
		=> MutateRecurringAsync(name, schedule => schedule.IsPaused = false, cancellationToken);

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		return await context.Set<ImmediateRecurringJobEntity>()
			.AsNoTracking()
			.Where(schedule => !schedule.IsPaused && schedule.NextRunAt <= now)
			.OrderBy(schedule => schedule.NextRunAt)
			.Take(batchSize)
			.Select(schedule => new RecurringJobSchedule
			{
				Name = schedule.Name,
				JobName = schedule.JobName,
				Cron = schedule.Cron,
				TimeZone = schedule.TimeZone,
				IsCodeDefined = schedule.IsCodeDefined,
				IsPaused = schedule.IsPaused,
				NextRunAt = schedule.NextRunAt,
				LastRunAt = schedule.LastRunAt,
			})
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		ArgumentNullException.ThrowIfNull(job);
		await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var strategy = strategyContext.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(
			operationCancellationToken => MaterializeRecurringCoreAsync(
				schedule,
				job,
				nextRunAt,
				operationCancellationToken
			),
			cancellationToken
		).ConfigureAwait(false);
	}

	private async Task<bool> MaterializeRecurringCoreAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var entity = await context.Set<ImmediateRecurringJobEntity>()
			.SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken)
			.ConfigureAwait(false);
		if (entity is null || entity.IsPaused || entity.NextRunAt != schedule.NextRunAt)
			return false;

		entity.LastRunAt = schedule.NextRunAt;
		entity.NextRunAt = nextRunAt;
		entity.ConcurrencyStamp = Guid.NewGuid();
		_ = context.Add(ToEntity(job));
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (DbUpdateException)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return false;
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var rawCounts = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.GroupBy(job => job.State)
			.Select(group => new { State = group.Key, Count = group.LongCount() })
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var counts = Enum.GetValues<JobState>().ToDictionary(static state => state, static _ => 0L);
		foreach (var item in rawCounts)
			counts[item.State] = item.Count;

		var recurring = await context.Set<ImmediateRecurringJobEntity>()
			.AsNoTracking()
			.OrderBy(schedule => schedule.Name)
			.Select(schedule => new RecurringJobSchedule
			{
				Name = schedule.Name,
				JobName = schedule.JobName,
				Cron = schedule.Cron,
				TimeZone = schedule.TimeZone,
				IsCodeDefined = schedule.IsCodeDefined,
				IsPaused = schedule.IsPaused,
				NextRunAt = schedule.NextRunAt,
				LastRunAt = schedule.LastRunAt,
			})
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var cutoff = _timeProvider.GetUtcNow() - TimeSpan.FromMinutes(2);
		var servers = await context.Set<ImmediateJobServerEntity>()
			.AsNoTracking()
			.Where(server => server.LastHeartbeat >= cutoff)
			.OrderBy(server => server.WorkerId)
			.Select(server => new JobServerSnapshot { WorkerId = server.WorkerId, LastHeartbeat = server.LastHeartbeat, ActiveWorkers = server.ActiveWorkers, MaxWorkers = server.MaxWorkers })
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return new JobMonitoringSnapshot
		{
			CapturedAt = _timeProvider.GetUtcNow(),
			Counts = counts,
			Recurring = recurring,
			Servers = servers,
			Capabilities = this.GetCapabilities(),
		};
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var jobs = context.Set<ImmediateJobEntity>().AsNoTracking();
		if (query.Id is { } id)
			jobs = jobs.Where(job => job.Id == id);
		if (query.State is { } state)
			jobs = jobs.Where(job => job.State == state);
		if (!string.IsNullOrWhiteSpace(query.QueueName))
			jobs = jobs.Where(job => job.QueueName == query.QueueName);
		if (!string.IsNullOrWhiteSpace(query.JobName))
			jobs = jobs.Where(job => job.JobName == query.JobName);
		if (!string.IsNullOrWhiteSpace(query.Search))
		{
			var search = query.Search.ToUpperInvariant();
			// The parameterless form is intentionally used because relational providers translate it to SQL.
#pragma warning disable CA1304, CA1311, CA1862, MA0011
			jobs = jobs.Where(job => job.JobName.ToUpper().Contains(search));
#pragma warning restore CA1304, CA1311, CA1862, MA0011
		}

		var entities = await jobs.OrderByDescending(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return [.. entities.Select(ToRecord)];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(query);
		query.Validate();
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == query.JobId, cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
			return [];

		var executions = context.Set<ImmediateJobExecutionEntity>()
			.AsNoTracking()
			.Where(execution => execution.JobId == query.JobId);
		if (query.Attempt is { } attempt)
			executions = executions.Where(execution => execution.Attempt == attempt);

		var synthetic = JobExecutionRecord.CreateSynthetic(ToRecord(job));
		var syntheticMissing = synthetic is not null
			&& (query.Attempt is null || query.Attempt == synthetic.Attempt)
			&& !await executions.AnyAsync(
				execution => execution.Attempt == synthetic.Attempt,
				cancellationToken
			).ConfigureAwait(false);
		var skip = query.Skip;
		var take = Math.Min(query.Take, JobExecutionQuery.MaximumTake);
		var result = new List<JobExecutionRecord>(take);
		if (syntheticMissing && skip == 0 && take != 0)
		{
			result.Add(synthetic!);
			take--;
		}
		else if (syntheticMissing)
		{
			skip--;
		}

		if (take != 0 && skip >= 0)
		{
			var persisted = await executions
				.OrderByDescending(execution => execution.Attempt)
				.Skip(skip)
				.Take(take)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			result.AddRange(persisted.Select(ToRecord));
		}

		return result;
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchStatusAsync(
		string batchId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
			.ConfigureAwait(false);
		return batch is null ? null : ToStatus(batch);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(
		IReadOnlyCollection<string> childJobIds,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(childJobIds);
		foreach (var childJobId in childJobIds)
			ArgumentException.ThrowIfNullOrWhiteSpace(childJobId);
		if (childJobIds.Count == 0)
			return [];

		var ids = childJobIds.Distinct(StringComparer.Ordinal).ToArray();
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var edges = await context.Set<ImmediateJobContinuationEntity>()
			.AsNoTracking()
			.Where(edge => ids.Contains(edge.ChildJobId))
			.OrderBy(edge => edge.ChildJobId)
			.ThenBy(edge => edge.ParentKind)
			.ThenBy(edge => edge.ParentId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return [.. edges.Select(ToContinuationEdge)];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var batches = context.Set<ImmediateJobBatchEntity>().AsNoTracking();
		if (query.State is { } state)
			batches = batches.Where(batch => batch.State == state);
		var entities = await batches.OrderByDescending(batch => batch.CreatedAt)
			.ThenBy(batch => batch.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return [.. entities.Select(ToStatus)];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var jobs = context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.Where(job => job.BatchId == batchId);
		if (query.State is { } state)
			jobs = jobs.Where(job => job.State == state);
		return await jobs.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.Select(job => new BatchMemberStatus
			{
				JobId = job.Id,
				JobName = job.JobName,
				QueueName = job.QueueName,
				State = job.State,
				Attempt = job.Attempt,
				CreatedAt = job.CreatedAt,
				CompletedAt = job.CompletedAt,
				LastError = job.LastError,
			})
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		string batchId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		if (!await context.Set<ImmediateJobBatchEntity>()
			.AnyAsync(batch => batch.Id == batchId, cancellationToken)
			.ConfigureAwait(false))
		{
			return null;
		}

		var jobs = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.Where(job => job.BatchId == batchId)
			.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Select(job => new BatchGraphNode { JobId = job.Id, JobName = job.JobName, State = job.State })
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var ids = jobs.Select(static job => job.JobId).ToArray();
		var edges = ids.Length == 0
			? []
			: await context.Set<ImmediateJobContinuationEntity>()
				.AsNoTracking()
				.Where(edge => ids.Contains(edge.ChildJobId))
				.OrderBy(edge => edge.ChildJobId)
				.ThenBy(edge => edge.ParentKind)
				.ThenBy(edge => edge.ParentId)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
		return new BatchGraph { BatchId = batchId, Nodes = jobs, Edges = [.. edges.Select(ToGraphEdge)] };
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		string jobId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
			return null;
		var edges = await context.Set<ImmediateJobContinuationEntity>()
			.AsNoTracking()
			.Where(edge => edge.ChildJobId == jobId)
			.OrderBy(edge => edge.ParentKind)
			.ThenBy(edge => edge.ParentId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return new JobStatus
		{
			JobId = job.Id,
			JobName = job.JobName,
			QueueName = job.QueueName,
			State = job.State,
			Attempt = job.Attempt,
			MaxAttempts = null,
			CreatedAt = job.CreatedAt,
			DueAt = job.DueAt,
			CompletedAt = job.CompletedAt,
			LastError = job.LastError,
			BatchId = job.BatchId,
			DependsOn = [.. edges.Select(ToGraphEdge)],
		};
	}

	/// <inheritdoc />
	public ValueTask CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		return RetryConcurrencyAsync(
			operationCancellationToken => CancelBatchCoreAsync(batchId, operationCancellationToken),
			cancellationToken
		);
	}

	private async Task CancelBatchCoreAsync(string batchId, CancellationToken cancellationToken)
	{
		var now = _timeProvider.GetUtcNow();
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
			.ConfigureAwait(false)
			?? throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
		if (batch.State != BatchState.Executing)
			throw new ImmediateJobException("Only an executing batch can be cancelled.");

		var jobs = await context.Set<ImmediateJobEntity>()
			.Where(job => job.BatchId == batchId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var jobsToCancel = jobs.Where(job => !IsTerminal(job.State)).ToArray();
		foreach (var job in jobsToCancel)
		{
			if (job.State == JobState.Active)
			{
				var execution = await GetOrMaterializeExecutionAsync(context, job, cancellationToken).ConfigureAwait(false)
					?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
				execution.State = JobExecutionState.Cancelled;
				execution.CompletedAt = now;
				execution.Error = null;
			}

			job.State = JobState.Cancelled;
			job.CompletedAt = now;
			job.WorkerId = null;
			job.LeaseExpiresAt = null;
			job.ConcurrencyStamp = Guid.NewGuid();
		}

		foreach (var job in jobsToCancel)
			await PropagateTerminalAsync(context, job, now, cancellationToken).ConfigureAwait(false);

		var terminalGroups = GetTerminalFairQueueGroups(context);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		foreach (var (queueName, groupId) in terminalGroups)
		{
			await TryRemoveFairQueueCursorAsync(queueName, groupId, CancellationToken.None).ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	public ValueTask DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		return ExecuteWithStrategyAsync(
			operationCancellationToken => DeleteBatchCoreAsync(batchId, operationCancellationToken),
			cancellationToken
		);
	}

	private async Task DeleteBatchCoreAsync(string batchId, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
			.ConfigureAwait(false)
			?? throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
		if (batch.State == BatchState.Executing)
			throw new ImmediateJobException("Only a terminal batch can be deleted.");

		var jobs = await context.Set<ImmediateJobEntity>()
			.Where(job => job.BatchId == batchId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var jobIds = jobs.Select(static job => job.Id).ToArray();
		var edges = await context.Set<ImmediateJobContinuationEntity>()
			.Where(edge =>
				jobIds.Contains(edge.ChildJobId)
				|| (edge.ParentKind == ContinuationParentKind.Job && jobIds.Contains(edge.ParentId))
				|| (edge.ParentKind == ContinuationParentKind.Batch && edge.ParentId == batchId))
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		context.RemoveRange(edges);
		context.RemoveRange(jobs);
		_ = context.Remove(batch);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public ValueTask CancelAsync(string jobId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		return RetryConcurrencyAsync(
			operationCancellationToken => CancelCoreAsync(jobId, operationCancellationToken),
			cancellationToken
		);
	}

	private async Task CancelCoreAsync(string jobId, CancellationToken cancellationToken)
	{
		var now = _timeProvider.GetUtcNow();
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
			.ConfigureAwait(false)
			?? throw new KeyNotFoundException($"Job '{jobId}' was not found.");
		if (IsTerminal(job.State))
			throw new ImmediateJobException("Only a non-terminal job can be cancelled.");

		if (job.State == JobState.Active)
		{
			var execution = await GetOrMaterializeExecutionAsync(context, job, cancellationToken).ConfigureAwait(false)
				?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
			execution.State = JobExecutionState.Cancelled;
			execution.CompletedAt = now;
			execution.Error = null;
		}

		job.State = JobState.Cancelled;
		job.CompletedAt = now;
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.ConcurrencyStamp = Guid.NewGuid();
		await PropagateTerminalAsync(context, job, now, cancellationToken).ConfigureAwait(false);

		var terminalGroups = GetTerminalFairQueueGroups(context);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		foreach (var (queueName, groupId) in terminalGroups)
		{
			await TryRemoveFairQueueCursorAsync(queueName, groupId, CancellationToken.None).ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	public ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		return RetryConcurrencyAsync(
			operationCancellationToken => RetryCoreAsync(jobId, operationCancellationToken),
			cancellationToken
		);
	}

	private async Task RetryCoreAsync(string jobId, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobId &&
				(item.State == JobState.Failed || item.State == JobState.Scheduled), cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
		{
			if (await context.Set<ImmediateJobEntity>()
				.AnyAsync(item => item.Id == jobId, cancellationToken)
				.ConfigureAwait(false))
			{
				throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");
			}

			throw new KeyNotFoundException($"Job '{jobId}' was not found.");
		}

		var wasFailed = job.State == JobState.Failed;
		_ = await GetOrMaterializeExecutionAsync(context, job, cancellationToken).ConfigureAwait(false);
		if (wasFailed && job.BatchId is { } batchId)
		{
			var batch = await context.Set<ImmediateJobBatchEntity>()
				.SingleAsync(item => item.Id == batchId, cancellationToken)
				.ConfigureAwait(false);
			batch.PendingCount++;
			batch.FailedCount = Math.Max(0, batch.FailedCount - 1);
			batch.State = BatchState.Executing;
			batch.CompletedAt = null;
			batch.ConcurrencyStamp = Guid.NewGuid();
		}

		job.State = JobState.Pending;
		job.DueAt = _timeProvider.GetUtcNow();
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		if (wasFailed)
		{
			job.CompletedAt = null;
			job.LastError = null;
		}

		job.ConcurrencyStamp = Guid.NewGuid();
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException)
		{
			if (!await context.Set<ImmediateJobEntity>()
				.AsNoTracking()
				.AnyAsync(item => item.Id == jobId, cancellationToken)
				.ConfigureAwait(false))
			{
				throw new KeyNotFoundException($"Job '{jobId}' was not found.");
			}

			throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		return ExecuteWithStrategyAsync(
			operationCancellationToken => DeleteCoreAsync(jobId, operationCancellationToken),
			cancellationToken
		);
	}

	private async Task DeleteCoreAsync(string jobId, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == jobId
				&& (item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled || item.State == JobState.Skipped), cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
		{
			if (await context.Set<ImmediateJobEntity>()
				.AnyAsync(item => item.Id == jobId, cancellationToken)
				.ConfigureAwait(false))
			{
				throw new ImmediateJobException("Only terminal jobs can be deleted.");
			}

			throw new KeyNotFoundException($"Job '{jobId}' was not found.");
		}

		if (job.BatchId is not null)
			throw new ImmediateJobException("Batch members are deleted with their batch so the workflow remains coherent.");
		_ = await context.Set<ImmediateJobContinuationEntity>()
			.Where(edge => edge.ChildJobId == jobId ||
				(edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == jobId))
			.ExecuteDeleteAsync(cancellationToken)
			.ConfigureAwait(false);
		var removed = await context.Set<ImmediateJobEntity>()
			.Where(item => item.Id == jobId &&
				(item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled || item.State == JobState.Skipped))
			.ExecuteDeleteAsync(cancellationToken)
			.ConfigureAwait(false);
		if (removed == 0)
		{
			if (await context.Set<ImmediateJobEntity>()
				.AnyAsync(item => item.Id == jobId, cancellationToken)
				.ConfigureAwait(false))
			{
				throw new ImmediateJobException("Only terminal jobs can be deleted.");
			}

			throw new KeyNotFoundException($"Job '{jobId}' was not found.");
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		var now = _timeProvider.GetUtcNow();
		return ExecuteWithStrategyAsync(
			operationCancellationToken => PurgeJobsCoreAsync(
				now - succeededRetention,
				now - failedRetention,
				operationCancellationToken
			),
			cancellationToken
		);
	}

	/// <inheritdoc />
	public ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		var now = _timeProvider.GetUtcNow();
		return ExecuteWithStrategyAsync(
			operationCancellationToken => PurgeBatchesCoreAsync(
				now - batchSucceededRetention,
				now - batchFailedRetention,
				operationCancellationToken
			),
			cancellationToken
		);
	}

	private async Task PurgeJobsCoreAsync(
		DateTimeOffset succeededBefore,
		DateTimeOffset failedBefore,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var jobs = await context.Set<ImmediateJobEntity>()
			.Where(job => job.BatchId == null
				&& ((job.State == JobState.Succeeded && job.CompletedAt < succeededBefore)
				|| ((job.State == JobState.Failed || job.State == JobState.Cancelled || job.State == JobState.Skipped) && job.CompletedAt < failedBefore))
			)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		if (jobs.Count != 0)
		{
			var jobIds = jobs.Select(static job => job.Id).ToArray();
			var edges = await context.Set<ImmediateJobContinuationEntity>()
				.Where(edge =>
					jobIds.Contains(edge.ChildJobId)
					|| (jobIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
				)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			context.RemoveRange(edges);
		}

		context.RemoveRange(jobs);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task PurgeBatchesCoreAsync(
		DateTimeOffset batchSucceededBefore,
		DateTimeOffset batchFailedBefore,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var batches = await context.Set<ImmediateJobBatchEntity>()
			.Where(batch => (batch.State == BatchState.Succeeded && batch.CompletedAt < batchSucceededBefore)
				|| ((batch.State == BatchState.Failed || batch.State == BatchState.Cancelled)
					&& batch.CompletedAt < batchFailedBefore))
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		if (batches.Count != 0)
		{
			var batchIds = batches.Select(static batch => batch.Id).ToArray();
			var memberIds = await context.Set<ImmediateJobEntity>()
				.Where(job => job.BatchId != null && batchIds.Contains(job.BatchId))
				.Select(job => job.Id)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			var edges = await context.Set<ImmediateJobContinuationEntity>()
				.Where(edge =>
					(batchIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Batch)
					|| memberIds.Contains(edge.ChildJobId)
					|| (memberIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
				)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			context.RemoveRange(edges);
			context.RemoveRange(batches);
		}

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var cutoff = _timeProvider.GetUtcNow() - TimeSpan.FromMinutes(2);
		_ = await context.Set<ImmediateJobServerEntity>()
			.Where(item => item.LastHeartbeat < cutoff)
			.ExecuteDeleteAsync(cancellationToken)
			.ConfigureAwait(false);
		var entity = await context.Set<ImmediateJobServerEntity>().FindAsync([server.WorkerId], cancellationToken).ConfigureAwait(false);
		if (entity is null)
		{
			_ = context.Add(new ImmediateJobServerEntity
			{
				WorkerId = server.WorkerId,
				LastHeartbeat = server.LastHeartbeat,
				ActiveWorkers = server.ActiveWorkers,
				MaxWorkers = server.MaxWorkers,
			});
		}
		else
		{
			entity.LastHeartbeat = server.LastHeartbeat;
			entity.ActiveWorkers = server.ActiveWorkers;
			entity.MaxWorkers = server.MaxWorkers;
		}

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
			return await context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is DbException or InvalidOperationException)
		{
			return false;
		}
	}

	private ValueTask MutateOwnedWithDependenciesAsync(
		string jobId,
		int executionNumber,
		string workerId,
		string? error,
		DateTimeOffset? nextRetryAt,
		bool succeeded,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken
	) => RetryConcurrencyAsync(
		operationCancellationToken => MutateOwnedCoreAsync(
			jobId,
			executionNumber,
			workerId,
			error,
			nextRetryAt,
			succeeded,
			additions,
			operationCancellationToken
		),
		cancellationToken
	);

	private async ValueTask RetryConcurrencyAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken
	)
	{
		var concurrencyAttempt = 0;
		while (true)
		{
			try
			{
				await ExecuteWithStrategyAsync(operation, cancellationToken).ConfigureAwait(false);
				return;
			}
			catch (DbUpdateConcurrencyException) when (++concurrencyAttempt < MaxConcurrencyAttempts)
			{
				// The transaction rolled back, so the next attempt re-evaluates the graph from durable state.
			}
			catch (DbUpdateException exception) when (++concurrencyAttempt < MaxConcurrencyAttempts)
			{
				if (!await IsSyntheticExecutionInsertRaceAsync(exception, cancellationToken).ConfigureAwait(false))
					throw;
				// The failed context is disposed by the operation; retry with the execution inserted by the winner.
			}
		}
	}

	private async ValueTask ExecuteWithStrategyAsync(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken
	)
	{
		await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var strategy = strategyContext.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<bool> IsSyntheticExecutionInsertRaceAsync(
		DbUpdateException exception,
		CancellationToken cancellationToken
	)
	{
		var syntheticExecutions = exception.Entries
			.Select(static entry => entry.Entity)
			.OfType<ImmediateJobExecutionEntity>()
			.Where(static execution => execution.IsSynthetic)
			.DistinctBy(static execution => (execution.JobId, execution.Attempt))
			.ToArray();
		if (syntheticExecutions.Length == 0)
			return false;

		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
			foreach (var execution in syntheticExecutions)
			{
				if (!await context.Set<ImmediateJobExecutionEntity>()
					.AsNoTracking()
					.AnyAsync(
						item => item.JobId == execution.JobId && item.Attempt == execution.Attempt,
						cancellationToken
					)
					.ConfigureAwait(false))
				{
					return false;
				}
			}

			return true;
		}
		catch (Exception verificationException) when (
			verificationException is DbException or InvalidOperationException
		)
		{
			return false;
		}
	}

	private ValueTask MutateOwnedAsync(
		string jobId,
		int executionNumber,
		string workerId,
		Action<ImmediateJobEntity, ImmediateJobExecutionEntity> mutate,
		CancellationToken cancellationToken
	) => RetryConcurrencyAsync(
		operationCancellationToken => MutateOwnedOnceAsync(
			jobId,
			executionNumber,
			workerId,
			mutate,
			operationCancellationToken
		),
		cancellationToken
	);

	private async Task MutateOwnedOnceAsync(
		string jobId,
		int executionNumber,
		string workerId,
		Action<ImmediateJobEntity, ImmediateJobExecutionEntity> mutate,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobId && item.Attempt == executionNumber && item.State == JobState.Active && item.WorkerId == workerId, cancellationToken)
			.ConfigureAwait(false) ?? throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobId}'.");
		var execution = await GetOrMaterializeExecutionAsync(context, job, cancellationToken).ConfigureAwait(false)
			?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
		mutate(job, execution);
		job.ConcurrencyStamp = Guid.NewGuid();
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException exception)
		{
			throw new ImmediateJobException(
				$"Worker '{workerId}' does not own active job '{jobId}'.",
				exception
			);
		}
	}

	private async Task MutateOwnedCoreAsync(
		string jobId,
		int executionNumber,
		string workerId,
		string? error,
		DateTimeOffset? nextRetryAt,
		bool succeeded,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobId && item.Attempt == executionNumber && item.State == JobState.Active && item.WorkerId == workerId, cancellationToken)
			.ConfigureAwait(false) ?? throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobId}'.");
		var now = _timeProvider.GetUtcNow();
		var execution = await GetOrMaterializeExecutionAsync(context, job, cancellationToken).ConfigureAwait(false)
			?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
		execution.State = succeeded ? JobExecutionState.Succeeded : JobExecutionState.Failed;
		execution.CompletedAt = now;
		execution.Error = error;
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.LastError = error;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!succeeded && nextRetryAt is { } retryAt)
		{
			job.State = retryAt <= now ? JobState.Pending : JobState.Scheduled;
			job.DueAt = retryAt;
			job.CompletedAt = null;
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return;
		}

		if (succeeded && additions.Count != 0)
		{
			await FlushContinuationAdditionsAsync(context, job, additions, cancellationToken).ConfigureAwait(false);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		job.State = succeeded ? JobState.Succeeded : JobState.Failed;
		job.CompletedAt = now;
		await PropagateTerminalAsync(context, job, now, cancellationToken).ConfigureAwait(false);
		var terminalGroups = GetTerminalFairQueueGroups(context);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		foreach (var (queueName, groupId) in terminalGroups)
			await TryRemoveFairQueueCursorAsync(queueName, groupId, CancellationToken.None).ConfigureAwait(false);
	}

	private static (string QueueName, string GroupId)[] GetTerminalFairQueueGroups(TContext context) =>
		[
			.. context.ChangeTracker
				.Entries<ImmediateJobEntity>()
				.Select(static entry => entry.Entity)
				.Where(static job => job.GroupId is not null && IsTerminal(job.State))
				.Select(static job => (job.QueueName, GroupId: job.GroupId!))
				.Distinct(),
		];

	private async ValueTask TryRemoveFairQueueCursorAsync(
		string queueName,
		string groupId,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
			if (await HasLiveGroupJobsAsync(context, queueName, groupId, cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			var cursor = await context.Set<ImmediateFairQueueGroupEntity>()
				.SingleOrDefaultAsync(
					group => group.QueueName == queueName && group.GroupId == groupId,
					cancellationToken
				)
				.ConfigureAwait(false);
			if (cursor is null)
				return;

			_ = context.Remove(cursor);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (
			!cancellationToken.IsCancellationRequested
			&& exception is DbException or DbUpdateException
		)
		{
			// Cleanup is best-effort metadata maintenance and must not invalidate a committed transition.
		}
	}

	private static Task<bool> HasLiveGroupJobsAsync(
		TContext context,
		string queueName,
		string groupId,
		CancellationToken cancellationToken
	) => context.Set<ImmediateJobEntity>().AnyAsync(
		job => job.QueueName == queueName
			&& job.GroupId == groupId
			&& (job.State == JobState.Pending
				|| job.State == JobState.Scheduled
				|| job.State == JobState.Active),
		cancellationToken
	);

	private async Task AddBatchJobCoreAsync(
		string currentJobId,
		int executionNumber,
		JobRecord record,
		ContinuationOptions options,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var current = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(job => job.Id == currentJobId && job.Attempt == executionNumber && job.State == JobState.Active, cancellationToken)
			.ConfigureAwait(false)
			?? throw new ImmediateJobException($"The current active job '{currentJobId}' was not found.");
		if (current.BatchId is not { } batchId)
			throw new ImmediateJobException("The current job does not belong to a batch.");
		if (options is not (ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations))
			throw new ArgumentOutOfRangeException(nameof(options));
		if (!string.Equals(record.BatchId, batchId, StringComparison.Ordinal))
			throw new ImmediateJobException("The new job must belong to the current job's batch.");
		if (record.State is JobState.Active or JobState.AwaitingContinuation || IsTerminal(record.State))
			throw new ImmediateJobException($"Concurrent batch member '{record.Id}' has invalid state '{record.State}'.");

		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleAsync(item => item.Id == batchId && item.State == BatchState.Executing, cancellationToken)
			.ConfigureAwait(false);
		var job = ToEntity(record);
		_ = context.Add(job);
		batch.TotalJobs++;
		batch.PendingCount++;
		batch.ConcurrencyStamp = Guid.NewGuid();

		if (options == ContinuationOptions.BeforeContinuations)
		{
			var waiters = await GetActiveWaitersAsync(context, currentJobId, cancellationToken).ConfigureAwait(false);
			foreach (var waiter in waiters)
			{
				_ = context.Add(new ImmediateJobContinuationEntity
				{
					ChildJobId = waiter.Id,
					ParentKind = ContinuationParentKind.Job,
					ParentId = job.Id,
					Trigger = ContinuationTrigger.Success,
				});
				waiter.RemainingDependencies++;
				waiter.ConcurrencyStamp = Guid.NewGuid();
			}
		}

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task FlushContinuationAdditionsAsync(
		TContext context,
		ImmediateJobEntity current,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken
	)
	{
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var trackedAdditions = 0;
		foreach (var addition in additions)
		{
			ArgumentNullException.ThrowIfNull(addition);
			ArgumentNullException.ThrowIfNull(addition.Job);
			if (!ids.Add(addition.Job.Id))
				throw new ImmediateJobException("Buffered continuations contain duplicate job identifiers.");
			if (!Enum.IsDefined(addition.Trigger))
				throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation trigger.");
			if (addition.Job.State is not (JobState.Pending or JobState.Scheduled))
				throw new ImmediateJobException($"Dynamic continuation '{addition.Job.Id}' has invalid state '{addition.Job.State}'.");

			if (addition.Options == ContinuationOptions.Detached)
			{
				if (addition.Job.BatchId is not null)
					throw new ImmediateJobException("A detached continuation cannot belong to a batch.");
			}
			else if (addition.Options is ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations)
			{
				if (current.BatchId is null || !string.Equals(addition.Job.BatchId, current.BatchId, StringComparison.Ordinal))
					throw new ImmediateJobException("A batch-tracked continuation must belong to the current job's batch.");
				trackedAdditions++;
			}
			else
			{
				throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation option.");
			}
		}

		var waiters = additions.Any(static addition => addition.Options == ContinuationOptions.BeforeContinuations)
			? await GetActiveWaitersAsync(context, current.Id, cancellationToken).ConfigureAwait(false)
			: [];
		ImmediateJobBatchEntity? batch = null;
		if (trackedAdditions != 0)
		{
			if (current.BatchId is not { } batchId)
				throw new ImmediateJobException("The current job does not belong to a batch.");
			batch = await context.Set<ImmediateJobBatchEntity>()
				.SingleAsync(item => item.Id == batchId && item.State == BatchState.Executing, cancellationToken)
				.ConfigureAwait(false);
			batch.TotalJobs += trackedAdditions;
			batch.PendingCount += trackedAdditions;
			batch.ConcurrencyStamp = Guid.NewGuid();
		}

		foreach (var addition in additions)
		{
			var job = ToEntity(addition.Job with
			{
				State = JobState.AwaitingContinuation,
				RemainingDependencies = 1,
			});
			_ = context.Add(job);
			_ = context.Add(new ImmediateJobContinuationEntity
			{
				ChildJobId = job.Id,
				ParentKind = ContinuationParentKind.Job,
				ParentId = current.Id,
				Trigger = addition.Trigger,
			});

			if (addition.Options != ContinuationOptions.BeforeContinuations)
				continue;
			foreach (var waiter in waiters)
			{
				_ = context.Add(new ImmediateJobContinuationEntity
				{
					ChildJobId = waiter.Id,
					ParentKind = ContinuationParentKind.Job,
					ParentId = job.Id,
					Trigger = ContinuationTrigger.Success,
				});
				waiter.RemainingDependencies++;
				waiter.ConcurrencyStamp = Guid.NewGuid();
			}
		}
	}

	private static async Task<List<ImmediateJobEntity>> GetActiveWaitersAsync(
		TContext context,
		string currentJobId,
		CancellationToken cancellationToken
	)
	{
		var waiterIds = await context.Set<ImmediateJobContinuationEntity>()
			.Where(edge => edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == currentJobId)
			.Select(edge => edge.ChildJobId)
			.Distinct()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);
		return waiterIds.Length == 0
			? []
			: await context.Set<ImmediateJobEntity>()
				.Where(job => waiterIds.Contains(job.Id) && job.State == JobState.AwaitingContinuation)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
	}

	private static async Task PropagateTerminalAsync(
		TContext context,
		ImmediateJobEntity terminalJob,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var parents = new Queue<(ContinuationParentKind Kind, string Id, ContinuationParentOutcome Outcome)>();
		var processed = new HashSet<(ContinuationParentKind Kind, string Id)>();
		parents.Enqueue((
			ContinuationParentKind.Job,
			terminalJob.Id,
			GetParentOutcome(terminalJob.State)
		));
		await UpdateBatchForTerminalJobAsync(context, terminalJob, now, parents, cancellationToken).ConfigureAwait(false);

		while (parents.TryDequeue(out var parent))
		{
			if (!processed.Add((parent.Kind, parent.Id)))
				continue;
			var edges = await context.Set<ImmediateJobContinuationEntity>()
				.Where(edge => edge.ParentKind == parent.Kind
					&& edge.ParentId == parent.Id
					&& edge.ParentOutcome == ContinuationParentOutcome.Unsettled)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			foreach (var edge in edges)
			{
				edge.ParentOutcome = parent.Outcome;
				var child = await context.Set<ImmediateJobEntity>()
					.SingleOrDefaultAsync(job => job.Id == edge.ChildJobId, cancellationToken)
					.ConfigureAwait(false);
				if (child is null || IsTerminal(child.State))
					continue;

				if (child.State != JobState.AwaitingContinuation || child.RemainingDependencies <= 0)
					continue;
				child.RemainingDependencies--;
				if (parent.Outcome == ContinuationParentOutcome.Failed)
					child.FailedDependencies++;
				if (child.RemainingDependencies == 0)
				{
					var skip = await ShouldSkipSettledContinuationAsync(
						context,
						child.Id,
						cancellationToken
					).ConfigureAwait(false);
					if (skip)
					{
						child.State = JobState.Skipped;
						child.CompletedAt = now;
						parents.Enqueue((ContinuationParentKind.Job, child.Id, ContinuationParentOutcome.Other));
						await UpdateBatchForTerminalJobAsync(context, child, now, parents, cancellationToken).ConfigureAwait(false);
					}
					else
					{
						child.State = child.DueAt <= now ? JobState.Pending : JobState.Scheduled;
					}
				}

				child.ConcurrencyStamp = Guid.NewGuid();
			}
		}
	}

	private static async Task<bool> ShouldSkipSettledContinuationAsync(
		TContext context,
		string childJobId,
		CancellationToken cancellationToken
	)
	{
		var edges = await context.Set<ImmediateJobContinuationEntity>()
			.Where(edge => edge.ChildJobId == childJobId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var requiresFailure = false;
		var anyFailed = false;
		foreach (var edge in edges)
		{
			if (edge.Trigger == ContinuationTrigger.Success
				&& edge.ParentOutcome != ContinuationParentOutcome.Succeeded)
			{
				return true;
			}

			requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
			anyFailed |= edge.ParentOutcome == ContinuationParentOutcome.Failed;
		}

		return requiresFailure && !anyFailed;
	}

	private static async Task UpdateBatchForTerminalJobAsync(
		TContext context,
		ImmediateJobEntity job,
		DateTimeOffset now,
		Queue<(ContinuationParentKind Kind, string Id, ContinuationParentOutcome Outcome)> parents,
		CancellationToken cancellationToken
	)
	{
		if (job.BatchId is not { } batchId)
			return;
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleAsync(item => item.Id == batchId, cancellationToken)
			.ConfigureAwait(false);
		batch.PendingCount = Math.Max(0, batch.PendingCount - 1);
		switch (job.State)
		{
			case JobState.Succeeded:
				batch.SucceededCount++;
				break;
			case JobState.Failed:
				batch.FailedCount++;
				break;
			case JobState.Cancelled:
				batch.CancelledCount++;
				break;
			case JobState.Skipped:
				batch.SkippedCount++;
				break;
			case JobState.AwaitingContinuation:
			case JobState.AwaitingParameters:
			case JobState.Scheduled:
			case JobState.Pending:
			case JobState.Active:
				throw new ImmediateJobException($"Job '{job.Id}' is not terminal.");
			default:
				throw new ArgumentOutOfRangeException(nameof(job), job.State, "Unknown job state.");
		}

		batch.ConcurrencyStamp = Guid.NewGuid();
		if (batch.PendingCount != 0)
			return;
		batch.State = GetTerminalBatchState(batch.FailedCount, batch.CancelledCount);
		batch.CompletedAt = now;
		parents.Enqueue((
			ContinuationParentKind.Batch,
			batch.Id,
			GetParentOutcome(batch.State)
		));
	}

	private static async Task EvaluateInitialDependenciesAsync(
		TContext context,
		Dictionary<string, ImmediateJobEntity> jobs,
		ImmediateJobContinuationEntity[] edges,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var externalJobIds = edges
			.Where(edge => edge.ParentKind == ContinuationParentKind.Job && !jobs.ContainsKey(edge.ParentId))
			.Select(static edge => edge.ParentId)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
		var externalBatchIds = edges
			.Where(static edge => edge.ParentKind == ContinuationParentKind.Batch)
			.Select(static edge => edge.ParentId)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
		var externalJobEntities = externalJobIds.Length == 0
			? []
			: await context.Set<ImmediateJobEntity>()
				.Where(job => externalJobIds.Contains(job.Id))
				.OrderBy(static job => job.Id)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
		var externalBatchEntities = externalBatchIds.Length == 0
			? []
			: await context.Set<ImmediateJobBatchEntity>()
				.Where(batch => externalBatchIds.Contains(batch.Id))
				.OrderBy(static batch => batch.Id)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
		var externalJobs = externalJobEntities.ToDictionary(job => job.Id, StringComparer.Ordinal);
		var externalBatches = externalBatchEntities.ToDictionary(batch => batch.Id, StringComparer.Ordinal);
		if (externalJobs.Count != externalJobIds.Length || externalBatches.Count != externalBatchIds.Length)
			throw new ImmediateJobException("A continuation parent does not exist.");
		foreach (var parent in externalJobEntities.Where(parent => !IsTerminal(parent.State)))
			parent.ConcurrencyStamp = Guid.NewGuid();
		foreach (var parent in externalBatchEntities.Where(parent => parent.State == BatchState.Executing))
			parent.ConcurrencyStamp = Guid.NewGuid();

		var incoming = edges.ToLookup(static edge => edge.ChildJobId, StringComparer.Ordinal);
		var changed = true;
		while (changed)
		{
			changed = false;
			foreach (var job in jobs.Values)
			{
				var dependencies = incoming[job.Id];
				if (!dependencies.Any() || IsTerminal(job.State))
					continue;
				var remaining = 0;
				var failedDependencies = 0;
				var requiresFailure = false;
				var violated = false;
				foreach (var edge in dependencies)
				{
					var (terminal, parentSucceeded, parentFailed) = GetParentState(
						edge,
						jobs,
						externalJobs,
						externalBatches
					);
					requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
					if (!terminal)
					{
						remaining++;
						continue;
					}

					edge.ParentOutcome = GetParentOutcome(parentSucceeded, parentFailed);

					if (parentFailed)
						failedDependencies++;
					if (edge.Trigger == ContinuationTrigger.Success && !parentSucceeded)
						violated = true;
				}

				job.FailedDependencies = failedDependencies;
				if (violated || (remaining == 0 && requiresFailure && failedDependencies == 0))
				{
					job.State = JobState.Skipped;
					job.RemainingDependencies = 0;
					job.CompletedAt = now;
					changed = true;
				}
				else if (remaining == 0)
				{
					job.State = job.DueAt <= now ? JobState.Pending : JobState.Scheduled;
					job.RemainingDependencies = 0;
				}
				else
				{
					job.State = JobState.AwaitingContinuation;
					job.RemainingDependencies = remaining;
				}
			}
		}
	}

	private static (bool Terminal, bool Succeeded, bool Failed) GetParentState(
		ImmediateJobContinuationEntity edge,
		Dictionary<string, ImmediateJobEntity> jobs,
		Dictionary<string, ImmediateJobEntity> externalJobs,
		Dictionary<string, ImmediateJobBatchEntity> externalBatches
	)
	{
		if (edge.ParentKind == ContinuationParentKind.Batch)
		{
			var state = externalBatches[edge.ParentId].State;
			return (state != BatchState.Executing, state == BatchState.Succeeded, state == BatchState.Failed);
		}

		var jobState = jobs.TryGetValue(edge.ParentId, out var job) ? job.State : externalJobs[edge.ParentId].State;
		return (IsTerminal(jobState), jobState == JobState.Succeeded, jobState == JobState.Failed);
	}

	private static void ThrowIfCyclic(
		HashSet<string> jobIds,
		IReadOnlyList<ImmediateJobContinuationEntity> edges
	)
	{
		var indegree = jobIds.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
		var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var edge in edges.Where(edge =>
			edge.ParentKind == ContinuationParentKind.Job && jobIds.Contains(edge.ParentId)))
		{
			indegree[edge.ChildJobId]++;
			if (!children.TryGetValue(edge.ParentId, out var values))
				children[edge.ParentId] = values = [];
			values.Add(edge.ChildJobId);
		}

		var ready = new Queue<string>(indegree.Where(static pair => pair.Value == 0).Select(static pair => pair.Key));
		var visited = 0;
		while (ready.TryDequeue(out var parent))
		{
			visited++;
			if (!children.TryGetValue(parent, out var values))
				continue;
			foreach (var child in values)
			{
				if (--indegree[child] == 0)
					ready.Enqueue(child);
			}
		}

		if (visited != jobIds.Count)
			throw new ImmediateJobException("The continuation graph contains a dependency cycle.");
	}

	private static bool IsTerminal(JobState state) =>
		state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;

	private static ContinuationParentOutcome GetParentOutcome(JobState state) => state switch
	{
		JobState.Succeeded => ContinuationParentOutcome.Succeeded,
		JobState.Failed => ContinuationParentOutcome.Failed,
		JobState.AwaitingContinuation or
		JobState.AwaitingParameters or
		JobState.Scheduled or
		JobState.Pending or
		JobState.Active or
		JobState.Cancelled or
		JobState.Skipped => ContinuationParentOutcome.Other,
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown job state."),
	};

	private static ContinuationParentOutcome GetParentOutcome(BatchState state) => state switch
	{
		BatchState.Succeeded => ContinuationParentOutcome.Succeeded,
		BatchState.Failed => ContinuationParentOutcome.Failed,
		BatchState.Executing or BatchState.Cancelled => ContinuationParentOutcome.Other,
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown batch state."),
	};

	private static ContinuationParentOutcome GetParentOutcome(bool succeeded, bool failed) =>
		(succeeded, failed) switch
		{
			(true, _) => ContinuationParentOutcome.Succeeded,
			(_, true) => ContinuationParentOutcome.Failed,
			_ => ContinuationParentOutcome.Other,
		};

	private static BatchState GetTerminalBatchState(int failed, int cancelled) =>
		failed != 0 ? BatchState.Failed : cancelled != 0 ? BatchState.Cancelled : BatchState.Succeeded;

	private async ValueTask MutateRecurringAsync(string name, Action<ImmediateRecurringJobEntity> mutate, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var schedule = await context.Set<ImmediateRecurringJobEntity>().FindAsync([name], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
		mutate(schedule);
		schedule.ConcurrencyStamp = Guid.NewGuid();
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	private static Task<int> UpdateRecurringAsync(
		TContext context,
		RecurringJobSchedule schedule,
		CancellationToken cancellationToken
	)
	{
		var concurrencyStamp = Guid.NewGuid();
		return context.Set<ImmediateRecurringJobEntity>()
			.Where(entity => entity.Name == schedule.Name && (schedule.IsCodeDefined || !entity.IsCodeDefined))
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(entity => entity.JobName, schedule.JobName)
				.SetProperty(entity => entity.Cron, schedule.Cron)
				.SetProperty(entity => entity.TimeZone, schedule.TimeZone)
				.SetProperty(entity => entity.IsCodeDefined, schedule.IsCodeDefined)
				.SetProperty(entity => entity.NextRunAt, schedule.NextRunAt)
				.SetProperty(entity => entity.ConcurrencyStamp, concurrencyStamp),
				cancellationToken);
	}

	private static async Task ThrowIfReplacingCodeDefinedScheduleAsync(
		TContext context,
		RecurringJobSchedule schedule,
		CancellationToken cancellationToken
	)
	{
		if (!schedule.IsCodeDefined && await context.Set<ImmediateRecurringJobEntity>()
			.AnyAsync(entity => entity.Name == schedule.Name && entity.IsCodeDefined, cancellationToken)
			.ConfigureAwait(false))
		{
			throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules.");
		}
	}

	private static ImmediateJobEntity ToEntity(JobRecord job) => new()
	{
		Id = job.Id,
		QueueName = job.QueueName,
		JobName = job.JobName,
		GroupId = job.GroupId,
		Payload = job.Payload,
		Context = job.Context,
		State = job.State,
		DueAt = job.DueAt,
		CreatedAt = job.CreatedAt,
		Attempt = job.Attempt,
		WorkerId = job.WorkerId,
		LeaseExpiresAt = job.LeaseExpiresAt,
		LastError = job.LastError,
		CompletedAt = job.CompletedAt,
		RecurringKey = job.RecurringKey,
		TraceParent = job.TraceParent,
		TraceState = job.TraceState,
		ExecutionTraceId = job.ExecutionTraceId,
		ExecutionSpanId = job.ExecutionSpanId,
		ExecutionStartedAt = job.ExecutionStartedAt,
		BatchId = job.BatchId,
		RemainingDependencies = job.RemainingDependencies,
		FailedDependencies = job.FailedDependencies,
		ConcurrencyStamp = Guid.NewGuid(),
	};

	private static async Task PrepareAcquisitionExecutionsAsync(
		TContext context,
		ImmediateJobEntity candidate,
		string workerId,
		DateTimeOffset acquiredAt,
		CancellationToken cancellationToken
	)
	{
		var previous = await GetOrMaterializeExecutionAsync(context, candidate, cancellationToken).ConfigureAwait(false);
		if (candidate.State == JobState.Active && previous is not null)
		{
			previous.State = JobExecutionState.Interrupted;
			previous.CompletedAt = candidate.LeaseExpiresAt;
			previous.Error = null;
		}

		_ = context.Add(new ImmediateJobExecutionEntity
		{
			JobId = candidate.Id,
			Attempt = candidate.Attempt + 1,
			State = JobExecutionState.Active,
			WorkerId = workerId,
			AcquiredAt = acquiredAt,
		});
	}

	private static async Task<ImmediateJobExecutionEntity?> GetOrMaterializeExecutionAsync(
		TContext context,
		ImmediateJobEntity job,
		CancellationToken cancellationToken
	)
	{
		if (job.Attempt <= 0)
			return null;
		var execution = await context.Set<ImmediateJobExecutionEntity>()
			.SingleOrDefaultAsync(
				item => item.JobId == job.Id && item.Attempt == job.Attempt,
				cancellationToken
			)
			.ConfigureAwait(false);
		if (execution is not null)
			return execution;

		var synthetic = JobExecutionRecord.CreateSynthetic(ToRecord(job));
		if (synthetic is null)
			return null;
		execution = ToEntity(synthetic);
		_ = context.Add(execution);
		return execution;
	}

	private static ImmediateJobExecutionEntity ToEntity(JobExecutionRecord execution) => new()
	{
		JobId = execution.JobId,
		Attempt = execution.Attempt,
		State = execution.State,
		WorkerId = execution.WorkerId,
		AcquiredAt = execution.AcquiredAt,
		ExecutionStartedAt = execution.ExecutionStartedAt,
		CompletedAt = execution.CompletedAt,
		ExecutionTraceId = execution.ExecutionTraceId,
		ExecutionSpanId = execution.ExecutionSpanId,
		Error = execution.Error,
		IsSynthetic = execution.IsSynthetic,
	};

	private static JobExecutionRecord ToRecord(ImmediateJobExecutionEntity execution) => new()
	{
		JobId = execution.JobId,
		Attempt = execution.Attempt,
		State = execution.State,
		WorkerId = execution.WorkerId,
		AcquiredAt = execution.AcquiredAt,
		ExecutionStartedAt = execution.ExecutionStartedAt,
		CompletedAt = execution.CompletedAt,
		ExecutionTraceId = execution.ExecutionTraceId,
		ExecutionSpanId = execution.ExecutionSpanId,
		Error = execution.Error,
		IsSynthetic = execution.IsSynthetic,
	};

	private static ImmediateJobContinuationEntity ToEntity(JobContinuationEdge edge)
	{
		var hasJobParent = edge.ParentJobId is not null;
		var hasBatchParent = edge.ParentBatchId is not null;
		if (hasJobParent == hasBatchParent)
			throw new ImmediateJobException("A continuation edge must identify exactly one parent job or batch.");
		return new()
		{
			ChildJobId = edge.ChildJobId,
			ParentKind = hasJobParent ? ContinuationParentKind.Job : ContinuationParentKind.Batch,
			ParentId = edge.ParentJobId ?? edge.ParentBatchId!,
			Trigger = edge.Trigger,
		};
	}

	private static ImmediateJobEntity Copy(ImmediateJobEntity job) => new()
	{
		Id = job.Id,
		QueueName = job.QueueName,
		JobName = job.JobName,
		GroupId = job.GroupId,
		Payload = job.Payload,
		Context = job.Context,
		State = job.State,
		DueAt = job.DueAt,
		CreatedAt = job.CreatedAt,
		Attempt = job.Attempt,
		WorkerId = job.WorkerId,
		LeaseExpiresAt = job.LeaseExpiresAt,
		LastError = job.LastError,
		CompletedAt = job.CompletedAt,
		RecurringKey = job.RecurringKey,
		TraceParent = job.TraceParent,
		TraceState = job.TraceState,
		ExecutionTraceId = job.ExecutionTraceId,
		ExecutionSpanId = job.ExecutionSpanId,
		ExecutionStartedAt = job.ExecutionStartedAt,
		BatchId = job.BatchId,
		RemainingDependencies = job.RemainingDependencies,
		FailedDependencies = job.FailedDependencies,
		ConcurrencyStamp = job.ConcurrencyStamp,
	};

	private static JobRecord ToRecord(ImmediateJobEntity job) => new()
	{
		Id = job.Id,
		QueueName = job.QueueName,
		JobName = job.JobName,
		GroupId = job.GroupId,
		Payload = job.Payload,
		Context = job.Context,
		State = job.State,
		DueAt = job.DueAt,
		CreatedAt = job.CreatedAt,
		Attempt = job.Attempt,
		WorkerId = job.WorkerId,
		LeaseExpiresAt = job.LeaseExpiresAt,
		LastError = job.LastError,
		CompletedAt = job.CompletedAt,
		RecurringKey = job.RecurringKey,
		TraceParent = job.TraceParent,
		TraceState = job.TraceState,
		ExecutionTraceId = job.ExecutionTraceId,
		ExecutionSpanId = job.ExecutionSpanId,
		ExecutionStartedAt = job.ExecutionStartedAt,
		BatchId = job.BatchId,
		RemainingDependencies = job.RemainingDependencies,
		FailedDependencies = job.FailedDependencies,
	};

	private static BatchStatus ToStatus(ImmediateJobBatchEntity batch) => new()
	{
		Id = batch.Id,
		State = batch.State,
		Total = batch.TotalJobs,
		Succeeded = batch.SucceededCount,
		Failed = batch.FailedCount,
		Cancelled = batch.CancelledCount,
		Skipped = batch.SkippedCount,
		Remaining = batch.PendingCount,
		CreatedAt = batch.CreatedAt,
		StartedAt = batch.StartedAt,
		CompletedAt = batch.CompletedAt,
		FractionSettled = BatchStatus.CalculateFractionSettled(batch.TotalJobs, batch.PendingCount),
	};

	private static JobContinuationEdge ToContinuationEdge(ImmediateJobContinuationEntity edge) => new()
	{
		ChildJobId = edge.ChildJobId,
		ParentJobId = edge.ParentKind == ContinuationParentKind.Job ? edge.ParentId : null,
		ParentBatchId = edge.ParentKind == ContinuationParentKind.Batch ? edge.ParentId : null,
		Trigger = edge.Trigger,
	};

	private static BatchGraphEdge ToGraphEdge(ImmediateJobContinuationEntity edge) => new()
	{
		ChildJobId = edge.ChildJobId,
		ParentJobId = edge.ParentKind == ContinuationParentKind.Job ? edge.ParentId : null,
		ParentBatchId = edge.ParentKind == ContinuationParentKind.Batch ? edge.ParentId : null,
		Trigger = edge.Trigger,
	};

	private static ImmediateRecurringJobEntity ToEntity(RecurringJobSchedule schedule) => new()
	{
		Name = schedule.Name,
		JobName = schedule.JobName,
		Cron = schedule.Cron,
		TimeZone = schedule.TimeZone,
		IsCodeDefined = schedule.IsCodeDefined,
		IsPaused = schedule.IsPaused,
		NextRunAt = schedule.NextRunAt,
		LastRunAt = schedule.LastRunAt,
		ConcurrencyStamp = Guid.NewGuid(),
	};
}

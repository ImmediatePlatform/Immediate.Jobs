using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Immediate.Jobs.EntityFrameworkCore;

/// <summary>An optimistic-concurrency EF Core implementation of <see cref="IJobStorage"/>.</summary>
/// <typeparam name="TContext">The application context containing the Immediate.Jobs model.</typeparam>
public sealed class EntityFrameworkCoreJobStorage<TContext>(
	IDbContextFactory<TContext> contextFactory,
	TimeProvider? timeProvider = null
) : IJobStorage, IJobStorageReplica
	where TContext : DbContext
{
	private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		_ = context.Set<ImmediateJobEntity>().Add(ToEntity(job));
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
		JobBatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(batch);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(edges);
		if (jobs.Count == 0)
			throw new ImmediateJobException("IJOB016: An atomic batch cannot be committed without jobs.");
		await ExecuteGraphInsertAsync(batch, jobs, edges, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask ExecuteGraphInsertAsync(
		JobBatchRecord? batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken
	)
	{
		await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var strategy = strategyContext.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(
			operationCancellationToken => InsertGraphCoreAsync(batch, jobs, edges, operationCancellationToken),
			cancellationToken
		).ConfigureAwait(false);
	}

	private async Task InsertGraphCoreAsync(
		JobBatchRecord? batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken
	)
	{
		var jobIds = jobs.Select(static job => job.Id).ToHashSet(StringComparer.Ordinal);
		if (jobIds.Count != jobs.Count)
			throw new ImmediateJobException("A batch or continuation insert contains duplicate job identifiers.");
		if (batch is not null && jobs.Any(job => job.BatchId != batch.Id))
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
			_ = context.Add(new ImmediateJobBatchEntity
			{
				Id = batch.Id,
				CreatedAt = batch.CreatedAt,
				TotalJobs = jobEntities.Count,
				PendingCount = pending,
				SucceededCount = succeeded,
				FailedCount = failed,
				CancelledCount = cancelled,
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
			entity.State = JobState.Active;
			entity.WorkerId = workerId;
			entity.LeaseExpiresAt = now + lease;
			entity.Attempt++;
			entity.CompletedAt = null;
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
			catch (DbUpdateConcurrencyException)
			{
				// Another scheduler claimed or changed this candidate first.
			}
		}

		return acquired;
	}

	/// <inheritdoc />
	public ValueTask RenewLeaseAsync(string jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
		return MutateOwnedAsync(jobId, workerId, job => job.LeaseExpiresAt = _timeProvider.GetUtcNow() + lease, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask CompleteAsync(string jobId, string workerId, CancellationToken cancellationToken = default) =>
		CompleteWithContinuationsAsync(jobId, workerId, [], cancellationToken);

	/// <inheritdoc />
	public ValueTask CompleteWithContinuationsAsync(
		string jobId,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(additions);
		return MutateOwnedWithDependenciesAsync(
			jobId,
			workerId,
			error: null,
			nextRetryAt: null,
			succeeded: true,
			additions,
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask AddBatchJobAsync(
		string currentJobId,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(job);
		if (options == ContinuationOptions.Detached)
			throw new ImmediateJobException("IJOB020: AddToBatchAsync cannot create a detached job.");
		const int MaxConcurrencyAttempts = 5;
		for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
		{
			try
			{
				await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
				var strategy = strategyContext.Database.CreateExecutionStrategy();
				await strategy.ExecuteAsync(
					operationCancellationToken => AddBatchJobCoreAsync(
						currentJobId,
						job,
						options,
						operationCancellationToken
					),
					cancellationToken
				).ConfigureAwait(false);
				return;
			}
			catch (DbUpdateConcurrencyException) when (attempt + 1 < MaxConcurrencyAttempts)
			{
			}
		}
	}

	/// <inheritdoc />
	public ValueTask FailAsync(
		string jobId,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	) => MutateOwnedWithDependenciesAsync(jobId, workerId, error, nextRetryAt, succeeded: false, [], cancellationToken);

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
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var entity = await context.Set<ImmediateRecurringJobEntity>()
			.SingleOrDefaultAsync(schedule => schedule.Name == name && !schedule.IsCodeDefined, cancellationToken)
			.ConfigureAwait(false);
		if (entity is not null)
		{
			_ = context.Remove(entity);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
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
		var servers = await context.Set<ImmediateJobServerEntity>()
			.AsNoTracking()
			.OrderBy(server => server.WorkerId)
			.Select(server => new JobServerSnapshot(server.WorkerId, server.LastHeartbeat, server.ActiveWorkers, server.MaxWorkers))
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return new(_timeProvider.GetUtcNow(), counts, recurring, servers);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		IQueryable<ImmediateJobEntity> jobs = context.Set<ImmediateJobEntity>().AsNoTracking();
		if (query.Id is { } id)
			jobs = jobs.Where(job => job.Id == id);
		if (query.State is { } state)
			jobs = jobs.Where(job => job.State == state);
		if (!string.IsNullOrWhiteSpace(query.QueueName))
			jobs = jobs.Where(job => job.QueueName == query.QueueName);
		if (!string.IsNullOrWhiteSpace(query.Search))
		{
			var search = query.Search.ToUpperInvariant();
			// The parameterless form is intentionally used because relational providers translate it to SQL.
#pragma warning disable CA1304, CA1311, CA1862
			jobs = jobs.Where(job => job.JobName.ToUpper().Contains(search));
#pragma warning restore CA1304, CA1311, CA1862
		}

		var entities = await jobs.OrderByDescending(job => job.CreatedAt)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return [.. entities.Select(ToRecord)];
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
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		JobBatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		IQueryable<ImmediateJobBatchEntity> batches = context.Set<ImmediateJobBatchEntity>().AsNoTracking();
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
		IQueryable<ImmediateJobEntity> jobs = context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.Where(job => job.BatchId == batchId);
		if (query.State is { } state)
			jobs = jobs.Where(job => job.State == state);
		return await jobs.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.Select(job => new BatchMemberStatus(
				job.Id,
				job.JobName,
				job.QueueName,
				job.State,
				job.Attempt,
				job.CreatedAt,
				job.CompletedAt,
				job.LastError
			))
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
			.Select(job => new BatchGraphNode(job.Id, job.JobName, job.State))
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
		return new(batchId, jobs, [.. edges.Select(ToGraphEdge)]);
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
		return new(
			job.Id,
			job.JobName,
			job.QueueName,
			job.State,
			job.Attempt,
			MaxAttempts: 0,
			job.CreatedAt,
			job.DueAt,
			job.CompletedAt,
			job.LastError,
			job.BatchId,
			[.. edges.Select(ToGraphEdge)]
		);
	}

	/// <inheritdoc />
	public ValueTask CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		return ExecuteWithStrategyAsync(
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
		foreach (var job in jobs)
		{
			if (IsTerminal(job.State))
				continue;
			job.State = JobState.Cancelled;
			job.CompletedAt = now;
			job.WorkerId = null;
			job.LeaseExpiresAt = null;
			job.ConcurrencyStamp = Guid.NewGuid();
			await PropagateTerminalAsync(context, job, now, cancellationToken).ConfigureAwait(false);
		}

		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
			.Where(edge => jobIds.Contains(edge.ChildJobId)
				|| edge.ParentKind == ContinuationParentKind.Job && jobIds.Contains(edge.ParentId)
				|| edge.ParentKind == ContinuationParentKind.Batch && edge.ParentId == batchId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		context.RemoveRange(edges);
		context.RemoveRange(jobs);
		_ = context.Remove(batch);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default)
	{
		return ExecuteWithStrategyAsync(
			operationCancellationToken => RetryCoreAsync(jobId, operationCancellationToken),
			cancellationToken
		);
	}

	private async Task RetryCoreAsync(string jobId, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobId && item.State == JobState.Failed, cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
			return;
		if (job.BatchId is { } batchId)
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
		job.CompletedAt = null;
		job.LastError = null;
		job.ConcurrencyStamp = Guid.NewGuid();
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobId
				&& (item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled), cancellationToken)
			.ConfigureAwait(false);
		if (job is not null)
		{
			if (job.BatchId is not null)
				throw new ImmediateJobException("Batch members are deleted with their batch so the workflow remains coherent.");
			var outgoing = await context.Set<ImmediateJobContinuationEntity>()
				.Where(edge => edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == jobId)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			context.RemoveRange(outgoing);
			_ = context.Remove(job);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	public ValueTask PurgeAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		var now = _timeProvider.GetUtcNow();
		return ExecuteWithStrategyAsync(
			operationCancellationToken => PurgeCoreAsync(
				now - succeededRetention,
				now - failedRetention,
				now - batchSucceededRetention,
				now - batchFailedRetention,
				operationCancellationToken
			),
			cancellationToken
		);
	}

	private async Task PurgeCoreAsync(
		DateTimeOffset succeededBefore,
		DateTimeOffset failedBefore,
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
				.Where(edge => batchIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Batch
					|| memberIds.Contains(edge.ChildJobId)
					|| memberIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			context.RemoveRange(edges);
			context.RemoveRange(batches);
		}

		var jobs = await context.Set<ImmediateJobEntity>()
			.Where(job => job.BatchId == null
				&& ((job.State == JobState.Succeeded && job.CompletedAt < succeededBefore)
				|| ((job.State == JobState.Failed || job.State == JobState.Cancelled) && job.CompletedAt < failedBefore))
			)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		if (jobs.Count != 0)
		{
			var jobIds = jobs.Select(static job => job.Id).ToArray();
			var edges = await context.Set<ImmediateJobContinuationEntity>()
				.Where(edge => jobIds.Contains(edge.ChildJobId)
					|| jobIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			context.RemoveRange(edges);
		}

		context.RemoveRange(jobs);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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

	private async ValueTask MutateOwnedWithDependenciesAsync(
		string jobId,
		string workerId,
		string? error,
		DateTimeOffset? nextRetryAt,
		bool succeeded,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken
	)
	{
		const int MaxConcurrencyAttempts = 5;
		for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
		{
			try
			{
				await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
				var strategy = strategyContext.Database.CreateExecutionStrategy();
				await strategy.ExecuteAsync(
					operationCancellationToken => MutateOwnedCoreAsync(
						jobId,
						workerId,
						error,
						nextRetryAt,
						succeeded,
						additions,
						operationCancellationToken
					),
					cancellationToken
				).ConfigureAwait(false);
				return;
			}
			catch (DbUpdateConcurrencyException) when (attempt + 1 < MaxConcurrencyAttempts)
			{
				// A competing parent completion changed a shared child or batch counter. The whole
				// transaction rolled back, so retrying re-evaluates the graph from durable state.
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

	private async ValueTask MutateOwnedAsync(
		string jobId,
		string workerId,
		Action<ImmediateJobEntity> mutate,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobId && item.State == JobState.Active && item.WorkerId == workerId, cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
			return;
		mutate(job);
		job.ConcurrencyStamp = Guid.NewGuid();
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException)
		{
			// A stale worker must not update a lease that has been reclaimed.
		}
	}

	private async Task MutateOwnedCoreAsync(
		string jobId,
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
			.SingleOrDefaultAsync(item => item.Id == jobId && item.State == JobState.Active && item.WorkerId == workerId, cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
			return;

		var now = _timeProvider.GetUtcNow();
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
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task AddBatchJobCoreAsync(
		string currentJobId,
		JobRecord record,
		ContinuationOptions options,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var current = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(job => job.Id == currentJobId && job.State == JobState.Active, cancellationToken)
			.ConfigureAwait(false)
			?? throw new ImmediateJobException($"The current active job '{currentJobId}' was not found.");
		if (current.BatchId is not { } batchId)
			throw new ImmediateJobException("The current job does not belong to a batch.");
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleAsync(item => item.Id == batchId && item.State == BatchState.Executing, cancellationToken)
			.ConfigureAwait(false);
		var job = ToEntity(record with { BatchId = batchId });
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
		var ids = additions.Select(static addition => addition.Job.Id).ToHashSet(StringComparer.Ordinal);
		if (ids.Count != additions.Count)
			throw new ImmediateJobException("Buffered continuations contain duplicate job identifiers.");
		var waiters = additions.Any(static addition => addition.Options == ContinuationOptions.BeforeContinuations)
			? await GetActiveWaitersAsync(context, current.Id, cancellationToken).ConfigureAwait(false)
			: [];
		ImmediateJobBatchEntity? batch = null;
		var trackedAdditions = additions.Count(static addition => addition.Options != ContinuationOptions.Detached);
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
			var inBatch = addition.Options != ContinuationOptions.Detached;
			var job = ToEntity(addition.Job with
			{
				BatchId = inBatch ? current.BatchId : null,
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
		var parents = new Queue<(ContinuationParentKind Kind, string Id, bool Succeeded, bool Failed)>();
		var processed = new HashSet<(ContinuationParentKind Kind, string Id)>();
		parents.Enqueue((
			ContinuationParentKind.Job,
			terminalJob.Id,
			terminalJob.State == JobState.Succeeded,
			terminalJob.State == JobState.Failed
		));
		await UpdateBatchForTerminalJobAsync(context, terminalJob, now, parents, cancellationToken).ConfigureAwait(false);

		while (parents.TryDequeue(out var parent))
		{
			if (!processed.Add((parent.Kind, parent.Id)))
				continue;
			var edges = await context.Set<ImmediateJobContinuationEntity>()
				.Where(edge => edge.ParentKind == parent.Kind && edge.ParentId == parent.Id)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			foreach (var edge in edges)
			{
				var child = await context.Set<ImmediateJobEntity>()
					.SingleOrDefaultAsync(job => job.Id == edge.ChildJobId, cancellationToken)
					.ConfigureAwait(false);
				if (child is null || IsTerminal(child.State))
					continue;

				if (edge.Trigger == ContinuationTrigger.Success && !parent.Succeeded)
				{
					child.State = JobState.Cancelled;
					child.RemainingDependencies = 0;
					child.CompletedAt = now;
					child.WorkerId = null;
					child.LeaseExpiresAt = null;
					child.ConcurrencyStamp = Guid.NewGuid();
					parents.Enqueue((ContinuationParentKind.Job, child.Id, Succeeded: false, Failed: false));
					await UpdateBatchForTerminalJobAsync(context, child, now, parents, cancellationToken).ConfigureAwait(false);
					continue;
				}

				if (child.State != JobState.AwaitingContinuation || child.RemainingDependencies <= 0)
					continue;
				child.RemainingDependencies--;
				if (parent.Failed)
					child.FailedDependencies++;
				if (child.RemainingDependencies == 0)
				{
					if (edge.Trigger == ContinuationTrigger.Failure && child.FailedDependencies == 0)
					{
						child.State = JobState.Cancelled;
						child.CompletedAt = now;
						parents.Enqueue((ContinuationParentKind.Job, child.Id, Succeeded: false, Failed: false));
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

	private static async Task UpdateBatchForTerminalJobAsync(
		TContext context,
		ImmediateJobEntity job,
		DateTimeOffset now,
		Queue<(ContinuationParentKind Kind, string Id, bool Succeeded, bool Failed)> parents,
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
			batch.State == BatchState.Succeeded,
			batch.State == BatchState.Failed
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
			.ToArray();
		var externalBatchIds = edges
			.Where(static edge => edge.ParentKind == ContinuationParentKind.Batch)
			.Select(static edge => edge.ParentId)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		var externalJobs = externalJobIds.Length == 0
			? []
			: await context.Set<ImmediateJobEntity>()
				.AsNoTracking()
				.Where(job => externalJobIds.Contains(job.Id))
				.ToDictionaryAsync(job => job.Id, job => job.State, StringComparer.Ordinal, cancellationToken)
				.ConfigureAwait(false);
		var externalBatches = externalBatchIds.Length == 0
			? []
			: await context.Set<ImmediateJobBatchEntity>()
				.AsNoTracking()
				.Where(batch => externalBatchIds.Contains(batch.Id))
				.ToDictionaryAsync(batch => batch.Id, batch => batch.State, StringComparer.Ordinal, cancellationToken)
				.ConfigureAwait(false);
		if (externalJobs.Count != externalJobIds.Length || externalBatches.Count != externalBatchIds.Length)
			throw new ImmediateJobException("A continuation parent does not exist.");

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

					if (parentFailed)
						failedDependencies++;
					if (edge.Trigger == ContinuationTrigger.Success && !parentSucceeded)
						violated = true;
				}

				job.FailedDependencies = failedDependencies;
				if (violated || remaining == 0 && requiresFailure && failedDependencies == 0)
				{
					job.State = JobState.Cancelled;
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
		Dictionary<string, JobState> externalJobs,
		Dictionary<string, BatchState> externalBatches
	)
	{
		if (edge.ParentKind == ContinuationParentKind.Batch)
		{
			var state = externalBatches[edge.ParentId];
			return (state != BatchState.Executing, state == BatchState.Succeeded, state == BatchState.Failed);
		}

		var jobState = jobs.TryGetValue(edge.ParentId, out var job) ? job.State : externalJobs[edge.ParentId];
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
			throw new ImmediateJobException("IJOB018: The continuation graph contains a dependency cycle.");
	}

	private static bool IsTerminal(JobState state) =>
		state is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

	private static BatchState GetTerminalBatchState(int failed, int cancelled) =>
		failed != 0 ? BatchState.Failed : cancelled != 0 ? BatchState.Cancelled : BatchState.Succeeded;

	private async ValueTask MutateRecurringAsync(string name, Action<ImmediateRecurringJobEntity> mutate, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var schedule = await context.Set<ImmediateRecurringJobEntity>().FindAsync([name], cancellationToken).ConfigureAwait(false);
		if (schedule is null)
			return;
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
		BatchId = job.BatchId,
		RemainingDependencies = job.RemainingDependencies,
		FailedDependencies = job.FailedDependencies,
		ConcurrencyStamp = Guid.NewGuid(),
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
		BatchId = job.BatchId,
		RemainingDependencies = job.RemainingDependencies,
		FailedDependencies = job.FailedDependencies,
	};

	private static BatchStatus ToStatus(ImmediateJobBatchEntity batch) => new(
		batch.Id,
		batch.State,
		batch.TotalJobs,
		batch.SucceededCount,
		batch.FailedCount,
		batch.CancelledCount,
		batch.PendingCount,
		batch.CreatedAt,
		batch.StartedAt,
		batch.CompletedAt,
		batch.TotalJobs == 0 ? 1 : (double)(batch.TotalJobs - batch.PendingCount) / batch.TotalJobs
	);

	private static BatchGraphEdge ToGraphEdge(ImmediateJobContinuationEntity edge) => new(
		edge.ChildJobId,
		edge.ParentKind == ContinuationParentKind.Job ? edge.ParentId : null,
		edge.ParentKind == ContinuationParentKind.Batch ? edge.ParentId : null,
		edge.Trigger
	);

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

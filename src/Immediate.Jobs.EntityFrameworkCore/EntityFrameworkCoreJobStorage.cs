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
			?? throw new InvalidOperationException("Immediate.Jobs entities are not configured. Call modelBuilder.AddImmediateJobs() from OnModelCreating.");
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
		IReadOnlyCollection<Guid> jobIds,
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
	public ValueTask RenewLeaseAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
		return MutateOwnedAsync(jobId, workerId, job => job.LeaseExpiresAt = _timeProvider.GetUtcNow() + lease, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default)
		=> MutateOwnedAsync(jobId, workerId, job =>
		{
			job.State = JobState.Succeeded;
			job.CompletedAt = _timeProvider.GetUtcNow();
			job.WorkerId = null;
			job.LeaseExpiresAt = null;
		}, cancellationToken);

	/// <inheritdoc />
	public ValueTask FailAsync(Guid jobId, string workerId, string error, DateTimeOffset? nextRetryAt, CancellationToken cancellationToken = default)
		=> MutateOwnedAsync(jobId, workerId, job =>
		{
			var now = _timeProvider.GetUtcNow();
			if (nextRetryAt is null)
			{
				job.State = JobState.Failed;
				job.CompletedAt = now;
			}
			else
			{
				job.State = nextRetryAt <= now ? JobState.Pending : JobState.Scheduled;
				job.DueAt = nextRetryAt.Value;
				job.CompletedAt = null;
			}

			job.LastError = error;
			job.WorkerId = null;
			job.LeaseExpiresAt = null;
		}, cancellationToken);

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		if (await UpdateRecurringAsync(context, schedule, cancellationToken).ConfigureAwait(false) != 0)
			return;

		_ = context.Add(ToEntity(schedule));
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateException)
		{
			// A competing node inserted the same schedule after our update attempt.
			await using var retryContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
			if (await UpdateRecurringAsync(retryContext, schedule, cancellationToken).ConfigureAwait(false) == 0)
				throw;
		}
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
	public async ValueTask RetryAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobId && item.State == JobState.Failed, cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
			return;
		job.State = JobState.Pending;
		job.DueAt = _timeProvider.GetUtcNow();
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.CompletedAt = null;
		job.LastError = null;
		job.ConcurrencyStamp = Guid.NewGuid();
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var job = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(item => item.Id == jobId
				&& (item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled), cancellationToken)
			.ConfigureAwait(false);
		if (job is not null)
		{
			_ = context.Remove(job);
			_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	public async ValueTask PurgeAsync(TimeSpan succeededRetention, TimeSpan failedRetention, CancellationToken cancellationToken = default)
	{
		var now = _timeProvider.GetUtcNow();
		var succeededBefore = now - succeededRetention;
		var failedBefore = now - failedRetention;
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
		var jobs = await context.Set<ImmediateJobEntity>()
			.Where(job => (job.State == JobState.Succeeded && job.CompletedAt < succeededBefore)
				|| ((job.State == JobState.Failed || job.State == JobState.Cancelled) && job.CompletedAt < failedBefore))
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		context.RemoveRange(jobs);
		_ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

	private async ValueTask MutateOwnedAsync(Guid jobId, string workerId, Action<ImmediateJobEntity> mutate, CancellationToken cancellationToken)
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
			.Where(entity => entity.Name == schedule.Name)
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(entity => entity.JobName, schedule.JobName)
				.SetProperty(entity => entity.Cron, schedule.Cron)
				.SetProperty(entity => entity.TimeZone, schedule.TimeZone)
				.SetProperty(entity => entity.IsCodeDefined, schedule.IsCodeDefined)
				.SetProperty(entity => entity.NextRunAt, schedule.NextRunAt)
				.SetProperty(entity => entity.ConcurrencyStamp, concurrencyStamp),
				cancellationToken);
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
		ConcurrencyStamp = Guid.NewGuid(),
	};

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

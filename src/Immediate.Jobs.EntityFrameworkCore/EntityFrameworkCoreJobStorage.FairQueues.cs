using System.Data.Common;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	private const int MaxConsecutiveFailedFairClaims = 5;

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsFairAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken
	)
	{
		var fairQueues = request.FairQueues!;
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
				var eligibleNames = await FilterAcquisitionNamesAsync(jobCapacities, request!, cancellationToken);
				if (eligibleNames.Count == 0)
					break;

				var selection = await ReadWithStrategyAsync(
					(readContext, operationCancellationToken) => SelectFairCandidateAsync(
						readContext,
						queue.QueueName,
						eligibleNames,
						now,
						fairQueues,
						operationCancellationToken
					),
					cancellationToken
				);
				if (selection is null)
				{
					var claimed = await AcquireFairFastPathAsync(
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						request.WorkerId,
						request.Lease,
						now,
						cancellationToken,
						request
					);
					queueCapacity -= claimed.Count;
					acquired.AddRange(claimed);
					break;
				}

				var claimedJob = fairQueues.GroupRoundRobin
					? await AcquireFairCandidateAsync(
						selection.Job,
						request.WorkerId,
						request.Lease,
						now,
						selection.NextSequence,
						cancellationToken,
						request
					)
					: GetFirstOrDefault(await AcquireCandidatesAsync(
							[selection.Job],
							request.WorkerId,
							request.Lease,
							now,
							cancellationToken,
							request
						));
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

	private static async Task<FairQueueSelection?> SelectFairCandidateAsync(
		TContext readContext,
		string queueName,
		List<string> eligibleNames,
		DateTimeOffset now,
		FairQueuePolicy fairQueues,
		CancellationToken cancellationToken
	)
	{
		var eligibleQuery = readContext.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.Where(job => job.QueueName == queueName && eligibleNames.Contains(job.JobName) &&
				(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
					|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)));
		if (!await eligibleQuery
			.AnyAsync(static job => job.GroupId != null, cancellationToken))
			return null;

		var groupedHeads = await eligibleQuery
			.Where(static job => job.GroupId != null)
			.GroupBy(static job => job.GroupId)
			.Select(static group => group
				.OrderBy(job => job.DueAt)
				.ThenBy(job => job.CreatedAt)
				.ThenBy(job => job.Id)
				.First())
			.ToListAsync(cancellationToken);
		var ungroupedHead = await eligibleQuery
			.Where(static job => job.GroupId == null)
			.OrderBy(job => job.DueAt)
			.ThenBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.FirstOrDefaultAsync(cancellationToken);
		if (groupedHeads.Count == 0)
			return null;

		var activeQuery = readContext.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.Where(job => job.QueueName == queueName
				&& job.State == JobState.Active
				&& job.LeaseExpiresAt > now);
		var totalInflight = await activeQuery
			.CountAsync(cancellationToken);
		var groupedHeadIds = groupedHeads.Select(static job => job.Id).ToList();
		var cursorQuery = readContext.Set<ImmediateFairQueueGroupEntity>()
			.AsNoTracking()
			.Where(group => group.QueueName == queueName);
		var groupStateQuery = eligibleQuery
			.Where(job => groupedHeadIds.Contains(job.Id));
		var groupStates = fairQueues.GroupRoundRobin
			? await groupStateQuery
				.Select(job => new FairQueueCandidateState(
					job.Id,
					activeQuery.Count(active => active.GroupId == job.GroupId),
					cursorQuery
						.Where(cursor => cursor.GroupId == job.GroupId)
						.Select(static cursor => cursor.LastServedSequence)
						.FirstOrDefault()
				))
				.ToDictionaryAsync(static state => state.JobHandle, StringComparer.Ordinal, cancellationToken)
			: await groupStateQuery
				.Select(job => new FairQueueCandidateState(
					job.Id,
					activeQuery.Count(active => active.GroupId == job.GroupId),
					0
				))
				.ToDictionaryAsync(static state => state.JobHandle, StringComparer.Ordinal, cancellationToken);
		var nextSequence = 0L;
		if (fairQueues.GroupRoundRobin)
		{
			var maxSequence = await cursorQuery
				.MaxAsync(static group => (long?)group.LastServedSequence, cancellationToken);
			nextSequence = (maxSequence ?? 0) + 1;
		}

		var candidates = ungroupedHead is null ? groupedHeads : [.. groupedHeads, ungroupedHead];
		var ranked = candidates.Select(job =>
		{
			FairQueueCandidateState? state = null;
			if (job.GroupId is not null)
				_ = groupStates.TryGetValue(job.Id, out state);
			var noisy = IsNoisy(job.GroupId, state?.Inflight ?? 0, totalInflight, fairQueues);
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
			.ThenBy(candidate => fairQueues.GroupRoundRobin
				? candidate.LastServedSequence
				: 0)
			.ThenBy(static candidate => candidate.Job.DueAt)
			.ThenBy(static candidate => candidate.Job.CreatedAt)
			.ThenBy(static candidate => candidate.Job.Id, StringComparer.Ordinal)
			.First()
			.Job;

		return new FairQueueSelection(selected, nextSequence);
	}

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireFairFastPathAsync(
		string queueName,
		Dictionary<string, int> jobCapacities,
		int queueCapacity,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		CancellationToken cancellationToken,
		JobAcquisitionRequest? request = null
	)
	{
		var acquired = new List<JobRecord>(queueCapacity);
		while (queueCapacity > 0)
		{
			var eligibleNames = await FilterAcquisitionNamesAsync(jobCapacities, request!, cancellationToken);
			if (eligibleNames.Count == 0)
				break;

			var candidates = await ReadWithStrategyAsync(
				(readContext, operationCancellationToken) => readContext.Set<ImmediateJobEntity>()
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
				cancellationToken,
				request
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

	private async ValueTask<JobRecord?> AcquireFairCandidateAsync(
		ImmediateJobEntity candidate,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		long nextSequence,
		CancellationToken cancellationToken,
		JobAcquisitionRequest? request = null
	)
	{
		await EnsureDefinitionAsync(candidate.JobName, cancellationToken);
		await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
		var strategy = strategyContext.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(
			operationCancellationToken => AcquireFairCandidateCoreAsync(
				candidate,
				workerId,
				lease,
				now,
				nextSequence,
				operationCancellationToken,
				request
			),
			cancellationToken
		);
	}

	private async Task<JobRecord?> AcquireFairCandidateCoreAsync(
		ImmediateJobEntity candidate,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		long nextSequence,
		CancellationToken cancellationToken,
		JobAcquisitionRequest? request = null
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		if (!await ReserveDefinitionAsync(context, candidate.JobName, request, now, cancellationToken))
		{
			_ = await context.SaveChangesAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return null;
		}

		var entity = Copy(candidate);
		_ = context.Attach(entity);
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

		if (candidate.GroupId is { } groupId)
		{
			var group = await context.Set<ImmediateFairQueueGroupEntity>()
				.SingleOrDefaultAsync(
					item => item.QueueName == candidate.QueueName && item.GroupId == groupId,
					cancellationToken
				);
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
			_ = await context.SaveChangesAsync(cancellationToken);
			await RefreshAcquiredDefinitionAsync(context, candidate.JobName, request, now, cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return ToRecord(entity);
		}

		catch (DbUpdateConcurrencyException)
		{
			// A tracked job, group cursor, or batch header lost its optimistic-concurrency check.
			return null;
		}

		catch (DbUpdateException)
		{
			await transaction.RollbackAsync(cancellationToken);
			if (await CandidateWasClaimedAsync(candidate, cancellationToken)
				|| await FairQueueCursorAdvancedAsync(candidate, nextSequence, cancellationToken))
			{
				return null;
			}

			throw;
		}
	}

	private async ValueTask<bool> FairQueueCursorAdvancedAsync(
		ImmediateJobEntity candidate,
		long nextSequence,
		CancellationToken cancellationToken
	)
	{
		if (candidate.GroupId is not { } groupId)
			return false;

		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			return await context.Set<ImmediateFairQueueGroupEntity>()
				.AsNoTracking()
				.AnyAsync(
					group => group.QueueName == candidate.QueueName
						&& group.GroupId == groupId
						&& group.LastServedSequence >= nextSequence,
					cancellationToken
				);
		}

		catch (Exception exception) when (exception is DbException or InvalidOperationException)
		{
			return false;
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

	private sealed record FairQueueSelection(
		ImmediateJobEntity Job,
		long NextSequence
	);

	private sealed record FairQueueCandidateState(
		string JobHandle,
		int Inflight,
		long LastServedSequence
	);

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
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			if (await HasLiveGroupJobsAsync(context, queueName, groupId, cancellationToken))
			{
				return;
			}

			var cursor = await context.Set<ImmediateFairQueueGroupEntity>()
				.SingleOrDefaultAsync(
					group => group.QueueName == queueName && group.GroupId == groupId,
					cancellationToken
				);
			if (cursor is null)
				return;

			_ = context.Remove(cursor);
			_ = await context.SaveChangesAsync(cancellationToken);
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
}

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
			var ensured = new HashSet<string>(StringComparer.Ordinal);
			var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
			if (queueCapacity <= 0)
				continue;

			var jobCapacities = queue.JobCapacities.ToDictionary(
				static pair => pair.Key,
				static pair => pair.Value,
				StringComparer.OrdinalIgnoreCase
			);
			while (queueCapacity > 0)
			{
				var eligibleNames = await FilterAcquisitionNamesAsync(jobCapacities, cancellationToken);
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
					var claimed = await AcquireQueueInExistingOrderAsync(
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						request.WorkerId,
						request.Lease,
						now,
						cancellationToken,
						eligibleNames
					);
					queueCapacity -= claimed.Count;
					acquired.AddRange(claimed);
					break;
				}

				await EnsureDefinitionsAsync(eligibleNames.Where(ensured.Add), cancellationToken);
				var claimedJob = fairQueues.GroupRoundRobin
					? await AcquireFairCandidateAsync(
						selection.Job,
						queue.QueueName,
						request.WorkerId,
						request.Lease,
						now,
						selection.NextSequence,
						cancellationToken
					)
					: GetFirstOrDefault(await AcquireCandidatesAsync(
							[selection.Job],
							request.WorkerId,
							request.Lease,
							now,
							cancellationToken
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
		var eligibleQuery = JobsWithDefinitions(readContext)
			.AsNoTracking()
			.Where(job => job.QueueName == queueName && eligibleNames.Contains(job.JobName) &&
				(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
					|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)));
		var heads = await eligibleQuery
			.GroupBy(static job => new { Group = job.GroupId ?? "", HasGroup = job.GroupId != null })
			.Select(static group => group.OrderBy(job => job.DueAt).ThenBy(job => job.CreatedAt).ThenBy(job => job.Id).First())
			.ToListAsync(cancellationToken);
		var groupedHeads = heads.Where(static job => job.GroupId != null).ToList();
		if (groupedHeads.Count == 0)
			return null;

		var activeQuery = JobsWithDefinitions(readContext)
			.AsNoTracking()
			.Where(job => job.QueueName == queueName
				&& job.State == JobState.Active
				&& job.LeaseExpiresAt > now);
		var inflight = await activeQuery.GroupBy(job => job.GroupId)
			.Select(group => new { GroupId = group.Key, Count = group.Count() })
			.ToListAsync(cancellationToken);
		var totalInflight = inflight.Sum(static group => group.Count);
		var activeCounts = inflight.Where(static group => group.GroupId != null)
			.ToDictionary(static group => group.GroupId!, static group => group.Count, StringComparer.Ordinal);
		var groupIds = groupedHeads.Select(static job => job.GroupId!).ToList();
		var cursorQuery = readContext.Set<ImmediateFairQueueGroupEntity>()
			.AsNoTracking().Where(group => group.QueueName == queueName);
		Dictionary<string, long> served = fairQueues.GroupRoundRobin
			? await cursorQuery.Where(group => groupIds.Contains(group.GroupId))
				.ToDictionaryAsync(static group => group.GroupId, static group => group.LastServedSequence, StringComparer.Ordinal, cancellationToken)
			: [with(StringComparer.Ordinal)];
		var nextSequence = 0L;
		if (fairQueues.GroupRoundRobin)
		{
			var maxSequence = await cursorQuery
				.MaxAsync(static group => (long?)group.LastServedSequence, cancellationToken);
			nextSequence = (maxSequence ?? 0) + 1;
		}

		var ranked = heads.Select(job =>
		{
			var active = job.GroupId is { } groupId ? activeCounts.GetValueOrDefault(groupId) : 0;
			var noisy = IsNoisy(job.GroupId, active, totalInflight, fairQueues);
			return new
			{
				Job = job,
				Noisy = noisy,
				NoisyInflight = noisy ? active : 0,
				LastServedSequence = job.GroupId is { } servedGroup ? served.GetValueOrDefault(servedGroup) : 0,
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

	private async ValueTask<JobRecord?> AcquireFairCandidateAsync(
		JobQueryRow candidate,
		string queueName,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		long nextSequence,
		CancellationToken cancellationToken
	)
	{
		await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
		var strategy = strategyContext.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(
			operationCancellationToken => AcquireFairCandidateCoreAsync(
				candidate,
				queueName,
				workerId,
				lease,
				now,
				nextSequence,
				operationCancellationToken
			),
			cancellationToken
		);
	}

	private async Task<JobRecord?> AcquireFairCandidateCoreAsync(
		JobQueryRow candidate,
		string queueName,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		long nextSequence,
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
					item => item.QueueName == queueName && item.GroupId == groupId,
					cancellationToken
				);
			if (group is null)
			{
				context.Add(new ImmediateFairQueueGroupEntity
				{
					QueueName = queueName,
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
			await context.SaveChangesAsync(cancellationToken);
			await RefreshAcquiredDefinitionAsync(context, candidate.JobName, limits, now, cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return ToRecord(entity, candidate.QueueName);
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
		JobQueryRow candidate,
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
		JobQueryRow Job,
		long NextSequence
	);

	private static List<string> GetTerminalFairQueueGroups(TContext context) => context.ChangeTracker.Entries<ImmediateJobEntity>()
		.Where(entry => entry.Entity.GroupId != null && IsTerminal(entry.Entity.State))
		.Select(entry => entry.Entity.GroupId!)
		.Distinct(StringComparer.Ordinal)
		.ToList();

	private async ValueTask TryRemoveFairQueueCursorsAsync(List<string> groups, CancellationToken cancellationToken)
	{
		if (groups.Count == 0)
			return;
		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			await context.Set<ImmediateFairQueueGroupEntity>()
				.Where(group => groups.Contains(group.GroupId) && !JobsWithDefinitions(context).Any(job =>
					job.QueueName == group.QueueName && job.GroupId == group.GroupId &&
					(job.State == JobState.Pending || job.State == JobState.Scheduled || job.State == JobState.Active)))
				.ExecuteDeleteAsync(cancellationToken);
		}
		catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is DbException or DbUpdateException)
		{
			// Cleanup must not invalidate a committed transition.
		}
	}
}

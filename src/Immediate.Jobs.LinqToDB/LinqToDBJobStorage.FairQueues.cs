using System.Data.Common;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	private const int MaxConsecutiveFailedFairClaims = 5;

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsFairAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
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
				StringComparer.OrdinalIgnoreCase
			);
			while (queueCapacity > 0)
			{
				var eligibleNames = await FilterAcquisitionNamesAsync(jobCapacities, cancellationToken);
				if (eligibleNames.Count == 0)
					break;

				await using var scope = contextScope.GetScope(out var readConnection);

				var eligibleQuery = JobsWithDefinitions(readConnection)
					.Where(job => job.QueueName == queue.QueueName && eligibleNames.Contains(job.JobName) &&
						(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
							|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)));
				var activeQuery = JobsWithDefinitions(readConnection)
					.Where(job => job.QueueName == queue.QueueName && job.State == JobState.Active && job.LeaseExpiresAt > now);
				var cursorQuery = FairQueueGroups(readConnection).Where(group => group.QueueName == queue.QueueName);
				var roundRobin = request.FairQueues!.GroupRoundRobin;
				// Keep group comparisons in SQL so the provider's group-id collation is preserved.
				var heads = await eligibleQuery.GroupBy(static job => job.GroupId)
					.Select(static group => group.OrderBy(job => job.DueAt).ThenBy(job => job.CreatedAt).ThenBy(job => job.Id).First())
					.Select(job => new
					{
						Job = job,
						Inflight = job.GroupId == null ? 0 : activeQuery.Count(active => active.GroupId == job.GroupId),
						LastServedSequence = roundRobin && job.GroupId != null
							? cursorQuery.Where(cursor => cursor.GroupId == job.GroupId).Select(cursor => cursor.LastServedSequence).FirstOrDefault()
							: 0,
					}).ToListAsync(cancellationToken);
				var groupedHeads = heads.Where(static head => head.Job.GroupId != null).ToList();
				var ungroupedHead = heads.SingleOrDefault(static head => head.Job.GroupId == null);
				if (groupedHeads.Count == 0)
				{
					var fastPath = await AcquireQueueInExistingOrderAsync(
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						request.WorkerId,
						request.Lease,
						now,
						cancellationToken,
						eligibleNames
					);
					queueCapacity -= fastPath.Count;
					acquired.AddRange(fastPath);
					break;
				}

				// Ungrouped active jobs still contribute to the noisy-group denominator.
				var totalInflight = await activeQuery.CountAsync(cancellationToken);
				var nextSequence = roundRobin
					? checked((await cursorQuery.Select(group => (long?)group.LastServedSequence).MaxAsync(cancellationToken) ?? 0) + 1)
					: 0;
				var candidates = ungroupedHead is null ? groupedHeads : [.. groupedHeads, ungroupedHead];
				var ranked = candidates.Select(head =>
				{
					var noisy = IsNoisy(head.Job.GroupId, head.Inflight, totalInflight, request.FairQueues);
					return new
					{
						head.Job,
						Noisy = noisy,
						NoisyInflight = noisy ? head.Inflight : 0,
						head.LastServedSequence,
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
						queue.QueueName,
						request.WorkerId,
						request.Lease,
						now,
						nextSequence,
						cancellationToken
					)
					: GetFirstOrDefault(await AcquireCandidatesAsync(
							[selected],
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
		await EnsureDefinitionAsync(candidate.JobName, cancellationToken);
		await using var scope = contextScope.GetScope(out var connection);

		await connection.BeginTransactionAsync(cancellationToken);
		Guid? observedCursorStamp = null;
		var cursorWasMissing = false;
		try
		{
			if (await ReserveDefinitionAsync(connection, candidate.JobName, now, cancellationToken) is not { } reservation)
			{
				await connection.CommitTransactionAsync(cancellationToken);
				return null;
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
				throw new LostRaceException();
			await PrepareAcquisitionExecutionsAsync(connection, previous, workerId, now, cancellationToken);

			if (candidate.BatchHandle is { } batchHandle)
			{
				await Batches(connection).Where(batch => batch.Id == batchHandle && batch.StartedAt == null)
					.Set(batch => batch.StartedAt, now)
					.Set(batch => batch.ConcurrencyStamp, Guid.NewGuid())
					.UpdateAsync(cancellationToken);
			}

			if (candidate.GroupId is { } groupId)
			{
				var cursor = await FairQueueGroups(connection)
					.SingleOrDefaultAsync(
						group => group.QueueName == queueName && group.GroupId == groupId,
						cancellationToken
					);
				if (cursor is null)
				{
					cursorWasMissing = true;
					await InsertAsync(connection, new ImmediateFairQueueGroupEntity
					{
						QueueName = queueName,
						GroupId = groupId,
						LastServedSequence = nextSequence,
						ConcurrencyStamp = Guid.NewGuid(),
					}, cancellationToken);
				}

				else if (cursor.LastServedSequence >= nextSequence)
				{
					throw new LostRaceException();
				}

				else
				{
					var cursorStamp = cursor.ConcurrencyStamp;
					observedCursorStamp = cursorStamp;
					cursor.LastServedSequence = nextSequence;
					cursor.ConcurrencyStamp = Guid.NewGuid();
					if (!await UpdateFairQueueGroupAsync(connection, cursor, cursorStamp, cancellationToken))
					{
						throw new LostRaceException();
					}
				}
			}

			await RefreshAcquiredDefinitionAsync(connection, reservation.Definition, reservation.Limits, now, cancellationToken);
			await connection.CommitTransactionAsync(cancellationToken);
			return ToRecord(candidate);
		}

		catch (SyntheticExecutionInsertFailedException exception)
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			if (await SyntheticExecutionExistsAsync(exception.JobHandle, exception.Attempt, cancellationToken))
				return null;
			throw exception.DatabaseException;
		}

		catch (LostRaceException)
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			return null;
		}

		catch (DbException)
		{
			try
			{
				await connection.RollbackTransactionAsync(cancellationToken);
			}

			catch (DbException)
			{
				// The original database error remains authoritative.
			}

			if (await FairQueueCursorChangedAsync(
				queueName,
				candidate.GroupId,
				observedCursorStamp,
				cursorWasMissing,
				cancellationToken
			))
			{
				return null;
			}

			throw;
		}
	}

	private async ValueTask<bool> FairQueueCursorChangedAsync(
		string queueName,
		string? groupId,
		Guid? observedCursorStamp,
		bool cursorWasMissing,
		CancellationToken cancellationToken
	)
	{
		if (groupId is null || (!cursorWasMissing && observedCursorStamp is null))
			return false;

		await using var scope = contextScope.GetScope(out var connection);

		var currentStamp = await FairQueueGroups(connection)
			.Where(group => group.QueueName == queueName && group.GroupId == groupId)
			.Select(static group => (Guid?)group.ConcurrencyStamp)
			.SingleOrDefaultAsync(cancellationToken);

		return cursorWasMissing
			? currentStamp is not null
			: currentStamp != observedCursorStamp;
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

	private async Task CleanupFairQueueGroupsAsync(IEnumerable<string> groups)
	{
		var groupIds = groups.Distinct(StringComparer.Ordinal).ToList();
		if (groupIds.Count == 0)
			return;

		try
		{
			await using var scope = contextScope.GetScope(out var connection);
			await FairQueueGroups(connection)
				.Where(group => groupIds.Contains(group.GroupId)
					&& !JobsWithDefinitions(connection).Any(job => job.QueueName == group.QueueName
						&& job.GroupId == group.GroupId
						&& (job.State == JobState.Pending
							|| job.State == JobState.Scheduled
							|| job.State == JobState.Active)))
				.DeleteAsync(CancellationToken.None);
		}
#pragma warning disable CA1031 // Cleanup cannot make an already committed job transition appear to fail.
		catch (Exception)
#pragma warning restore CA1031
		{
			// Cleanup is best-effort metadata maintenance and must not invalidate a committed transition.
		}
	}

	private static void AddFairQueueGroup(ISet<string> groups, ImmediateJobEntity job)
	{
		if (job.GroupId is { } groupId)
			groups.Add(groupId);
	}

	private async Task<bool> UpdateFairQueueGroupAsync(
		DataConnection connection,
		ImmediateFairQueueGroupEntity group,
		Guid oldStamp,
		CancellationToken cancellationToken
	)
	{
		var updated = await FairQueueGroups(connection)
			.Where(entity => entity.QueueName == group.QueueName
				&& entity.GroupId == group.GroupId
				&& entity.ConcurrencyStamp == oldStamp)
			.Set(entity => entity.LastServedSequence, group.LastServedSequence)
			.Set(entity => entity.ConcurrencyStamp, group.ConcurrencyStamp)
			.UpdateAsync(cancellationToken);
		return updated != 0;
	}
}

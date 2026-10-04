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
				StringComparer.Ordinal
			);
			while (queueCapacity > 0)
			{
				var eligibleNames = jobCapacities
					.Where(static pair => pair.Value > 0)
					.Select(static pair => pair.Key)
					.ToList();
				if (eligibleNames.Count == 0)
					break;

				await using var scope = contextScope.GetScope(out var readConnection);

				var eligibleQuery = Jobs(readConnection)
					.Where(job => job.QueueName == queue.QueueName && eligibleNames.Contains(job.JobName) &&
						(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
							|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)));
				if (!await eligibleQuery.AnyAsync(static job => job.GroupId != null, cancellationToken))
				{
					var fastPath = await AcquireFairFastPathAsync(
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						request.WorkerId,
						request.Lease,
						now,
						cancellationToken
					);
					queueCapacity -= fastPath.Count;
					acquired.AddRange(fastPath);
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
					.ToListAsync(cancellationToken);
				var ungroupedHead = await eligibleQuery
					.Where(static job => job.GroupId == null)
					.OrderBy(job => job.DueAt)
					.ThenBy(job => job.CreatedAt)
					.ThenBy(job => job.Id)
					.FirstOrDefaultAsync(cancellationToken);
				if (groupedHeads.Count == 0)
				{
					var fastPath = await AcquireFairFastPathAsync(
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						request.WorkerId,
						request.Lease,
						now,
						cancellationToken
					);
					queueCapacity -= fastPath.Count;
					acquired.AddRange(fastPath);
					break;
				}

				var activeQuery = Jobs(readConnection)
					.Where(job => job.QueueName == queue.QueueName
						&& job.State == JobState.Active
						&& job.LeaseExpiresAt > now);
				var totalInflight = await activeQuery.CountAsync(cancellationToken);
				var groupedHeadIds = groupedHeads.Select(static job => job.Id).ToList();
				var cursorQuery = FairQueueGroups(readConnection)
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
						.ToDictionaryAsync(static state => state.JobHandle, StringComparer.Ordinal, cancellationToken)
					: await groupStateQuery
						.Select(job => new FairQueueCandidateState(
							job.Id,
							activeQuery.Count(active => active.GroupId == job.GroupId),
							0
						))
						.ToDictionaryAsync(static state => state.JobHandle, StringComparer.Ordinal, cancellationToken);
				var nextSequence = 0L;
				if (request.FairQueues.GroupRoundRobin)
				{
					var maxSequence = await cursorQuery
						.Select(static group => (long?)group.LastServedSequence)
						.MaxAsync(cancellationToken);
					nextSequence = checked((maxSequence ?? 0) + 1);
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
				.ToList();
			if (eligibleNames.Count == 0)
				break;

			await using var scope = contextScope.GetScope(out var readConnection);

			var candidates = await Jobs(readConnection)
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
		CancellationToken cancellationToken
	)
	{
		await using var scope = contextScope.GetScope(out var connection);

		_ = await connection.BeginTransactionAsync(cancellationToken);
		Guid? observedCursorStamp = null;
		var cursorWasMissing = false;
		try
		{
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

			if (candidate.GroupId is { } groupId)
			{
				var cursor = await FairQueueGroups(connection)
					.SingleOrDefaultAsync(
						group => group.QueueName == candidate.QueueName && group.GroupId == groupId,
						cancellationToken
					);
				if (cursor is null)
				{
					cursorWasMissing = true;
					_ = await InsertAsync(connection, new ImmediateFairQueueGroupEntity
					{
						QueueName = candidate.QueueName,
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
				candidate.QueueName,
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

	private async Task ResetReturningGroupCursorsAsync(
		DataConnection connection,
		IReadOnlyCollection<JobRecord> jobs,
		CancellationToken cancellationToken
	)
	{
		var groups = jobs
			.Where(static job => job.GroupId is not null)
			.Select(static job => (job.QueueName, job.GroupId))
			.Distinct()
			.ToList();
		foreach (var (queueName, groupId) in groups)
		{
			var hasLiveJobs = await Jobs(connection)
				.AnyAsync(
					job => job.QueueName == queueName
						&& job.GroupId == groupId
						&& (job.State == JobState.Pending
							|| job.State == JobState.Scheduled
							|| job.State == JobState.Active),
					cancellationToken
				);
			if (hasLiveJobs)
				continue;

			_ = await FairQueueGroups(connection)
				.Where(group => group.QueueName == queueName && group.GroupId == groupId)
				.DeleteAsync(cancellationToken);
		}
	}

	private sealed record FairQueueCandidateState(
		string JobHandle,
		int Inflight,
		long LastServedSequence
	);

	private async Task CleanupFairQueueGroupsAsync(
		IEnumerable<(string QueueName, string GroupId)> groups
	)
	{
		foreach (var (queueName, groupId) in groups.Distinct())
		{
			try
			{
				await using var scope = contextScope.GetScope(out var connection);

				if (await Jobs(connection)
					.AnyAsync(
						item => item.QueueName == queueName
							&& item.GroupId == groupId
							&& (item.State == JobState.Pending
								|| item.State == JobState.Scheduled
								|| item.State == JobState.Active),
						CancellationToken.None
					))
				{
					continue;
				}

				_ = await FairQueueGroups(connection)
					.Where(group => group.QueueName == queueName && group.GroupId == groupId)
					.DeleteAsync(CancellationToken.None);
			}
#pragma warning disable CA1031 // Cleanup cannot make an already committed job transition appear to fail.
			catch (Exception)
#pragma warning restore CA1031
			{
				// Cleanup is best-effort metadata maintenance and must not invalidate a committed transition.
			}
		}
	}

	private static void AddFairQueueGroup(
		ISet<(string QueueName, string GroupId)> groups,
		ImmediateJobEntity job
	)
	{
		if (job.GroupId is { } groupId)
			_ = groups.Add((job.QueueName, groupId));
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

using System.Data.Common;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Immediate.Jobs.LinqToDB;

/// <summary>An optimistic-concurrency LinqToDB implementation of <see cref="IJobStorage"/>.</summary>
public sealed class LinqToDBJobStorage : IRecurringJobStorage, IJobGraphStorage, IJobStorageReplica
{
	private const int MaxConcurrencyAttempts = 5;
	private const int MaxConsecutiveFailedFairClaims = 5;
	private readonly DataOptions _dataOptions;
	private readonly string? _schema;
	private readonly TimeProvider _timeProvider;

	/// <summary>Creates storage using immutable LinqToDB connection options.</summary>
	/// <param name="dataOptions">The immutable LinqToDB connection options.</param>
	/// <param name="schema">The database schema containing the Immediate.Jobs tables, or <see langword="null"/> for the provider default.</param>
	/// <param name="timeProvider">The clock used for storage timestamps, or <see langword="null"/> to use the system clock.</param>
	public LinqToDBJobStorage(DataOptions dataOptions, string? schema = null, TimeProvider? timeProvider = null)
	{
		ArgumentNullException.ThrowIfNull(dataOptions);
		LinqToDBSchemaExtensions.ValidateSchema(schema);
		_dataOptions = dataOptions;
		_schema = schema;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <inheritdoc />
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;

	/// <inheritdoc />
	public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(job);
		await using var connection = CreateConnection();
		_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await ResetReturningGroupCursorsAsync(connection, [job], cancellationToken).ConfigureAwait(false);
			_ = await InsertAsync(connection, ToEntity(job), cancellationToken).ConfigureAwait(false);
			await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			throw;
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(
		IReadOnlyCollection<string> childJobIds,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(childJobIds);
		if (childJobIds.Any(string.IsNullOrWhiteSpace))
			throw new ArgumentException("Child job identifiers cannot be null or blank.", nameof(childJobIds));
		if (childJobIds.Count == 0)
			return [];

		var ids = childJobIds.Distinct(StringComparer.Ordinal).ToArray();
		await using var connection = CreateConnection();
		var edges = await Continuations(connection)
			.Where(edge => ids.Contains(edge.ChildJobId))
			.OrderBy(edge => edge.ChildJobId)
			.ThenBy(edge => edge.ParentKind)
			.ThenBy(edge => edge.ParentId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return [.. edges.Select(ToContinuationEdge)];
	}

	/// <inheritdoc />
	public ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(job);
		ArgumentNullException.ThrowIfNull(edges);
		return ExecuteGraphInsertAsync(batch: null, [job], edges, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask EnqueueBatchAsync(
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
			throw new ImmediateJobException("An atomic batch cannot be committed without jobs.");
		return ExecuteGraphInsertAsync(batch, jobs, edges, cancellationToken);
	}

	private async ValueTask ExecuteGraphInsertAsync(
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

		await using var connection = CreateConnection();
		_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var jobEntities = jobs.Select(ToEntity).ToDictionary(static job => job.Id, StringComparer.Ordinal);
			await ResetReturningGroupCursorsAsync(connection, jobs, cancellationToken).ConfigureAwait(false);
			await EvaluateInitialDependenciesAsync(
				connection,
				jobEntities,
				edgeEntities,
				_timeProvider.GetUtcNow().UtcTicks,
				cancellationToken
			).ConfigureAwait(false);

			if (batch is not null)
			{
				var terminal = jobEntities.Values.Where(static job => IsTerminal(job.State)).ToArray();
				var pending = jobEntities.Count - terminal.Length;
				var failed = terminal.Count(static job => job.State == JobState.Failed);
				var cancelled = terminal.Count(static job => job.State == JobState.Cancelled);
				_ = await InsertAsync(connection, new ImmediateJobBatchEntity
				{
					Id = batch.Id,
					CreatedAt = batch.CreatedAt.UtcTicks,
					TotalJobs = jobEntities.Count,
					PendingCount = pending,
					SucceededCount = terminal.Count(static job => job.State == JobState.Succeeded),
					FailedCount = failed,
					CancelledCount = cancelled,
					StartedAt = Ticks(batch.StartedAt),
					CompletedAt = pending == 0 ? Ticks(batch.CompletedAt ?? _timeProvider.GetUtcNow()) : null,
					State = pending == 0 ? GetTerminalBatchState(failed, cancelled) : BatchState.Executing,
					ConcurrencyStamp = Guid.NewGuid(),
				}, cancellationToken).ConfigureAwait(false);
			}

			foreach (var entity in jobEntities.Values)
				_ = await InsertAsync(connection, entity, cancellationToken).ConfigureAwait(false);
			foreach (var edge in edgeEntities)
				_ = await InsertAsync(connection, edge, cancellationToken).ConfigureAwait(false);
			await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			throw;
		}
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

		var now = _timeProvider.GetUtcNow().UtcTicks;
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
				await using var readConnection = CreateConnection();
				var candidates = await Jobs(readConnection)
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

				var selectionCapacities = new Dictionary<string, int>(jobCapacities, StringComparer.Ordinal);
				var selected = candidates.Where(candidate => selectionCapacities[candidate.JobName]-- > 0).ToList();
				var claimed = await AcquireCandidatesAsync(selected, request.WorkerId, request.Lease, now, cancellationToken)
					.ConfigureAwait(false);
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
		var now = _timeProvider.GetUtcNow().UtcTicks;
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

				await using var readConnection = CreateConnection();
				var eligibleQuery = Jobs(readConnection)
					.Where(job => job.QueueName == queue.QueueName && eligibleNames.Contains(job.JobName) &&
						(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
							|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)));
				if (!await eligibleQuery.AnyAsync(static job => job.GroupId != null, cancellationToken).ConfigureAwait(false))
				{
					var fastPath = await AcquireFairFastPathAsync(
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						request.WorkerId,
						request.Lease,
						now,
						cancellationToken
					).ConfigureAwait(false);
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
					var fastPath = await AcquireFairFastPathAsync(
						queue.QueueName,
						jobCapacities,
						queueCapacity,
						request.WorkerId,
						request.Lease,
						now,
						cancellationToken
					).ConfigureAwait(false);
					queueCapacity -= fastPath.Count;
					acquired.AddRange(fastPath);
					break;
				}

				var activeQuery = Jobs(readConnection)
					.Where(job => job.QueueName == queue.QueueName
						&& job.State == JobState.Active
						&& job.LeaseExpiresAt > now);
				var totalInflight = await activeQuery.CountAsync(cancellationToken).ConfigureAwait(false);
				var groupedHeadIds = groupedHeads.Select(static job => job.Id).ToArray();
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
						.ToDictionaryAsync(static state => state.JobId, cancellationToken)
						.ConfigureAwait(false)
					: await groupStateQuery
						.Select(job => new FairQueueCandidateState(
							job.Id,
							activeQuery.Count(active => active.GroupId == job.GroupId),
							0
						))
						.ToDictionaryAsync(static state => state.JobId, cancellationToken)
						.ConfigureAwait(false);
				var nextSequence = 0L;
				if (request.FairQueues.GroupRoundRobin)
				{
					var maxSequence = await cursorQuery
						.Select(static group => (long?)group.LastServedSequence)
						.MaxAsync(cancellationToken)
						.ConfigureAwait(false);
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
		long now,
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

			await using var readConnection = CreateConnection();
			var candidates = await Jobs(readConnection)
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
		long now,
		long nextSequence,
		CancellationToken cancellationToken
	)
	{
		await using var connection = CreateConnection();
		_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		Guid? observedCursorStamp = null;
		var cursorWasMissing = false;
		try
		{
			var oldStamp = candidate.ConcurrencyStamp;
			candidate.State = JobState.Active;
			candidate.WorkerId = workerId;
			candidate.LeaseExpiresAt = now + lease.Ticks;
			candidate.Attempt++;
			candidate.CompletedAt = null;
			candidate.ExecutionTraceId = null;
			candidate.ExecutionSpanId = null;
			candidate.ExecutionStartedAt = null;
			candidate.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, candidate, oldStamp, cancellationToken).ConfigureAwait(false))
				throw new LostRaceException();

			if (candidate.BatchId is { } batchId)
			{
				var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
					.ConfigureAwait(false);
				if (batch is not null && batch.StartedAt is null)
				{
					var batchStamp = batch.ConcurrencyStamp;
					batch.StartedAt = now;
					batch.ConcurrencyStamp = Guid.NewGuid();
					if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken).ConfigureAwait(false))
						throw new LostRaceException();
				}
			}

			if (candidate.GroupId is { } groupId)
			{
				var cursor = await FairQueueGroups(connection)
					.SingleOrDefaultAsync(
						group => group.QueueName == candidate.QueueName && group.GroupId == groupId,
						cancellationToken
					)
					.ConfigureAwait(false);
				if (cursor is null)
				{
					cursorWasMissing = true;
					_ = await InsertAsync(connection, new ImmediateFairQueueGroupEntity
					{
						QueueName = candidate.QueueName,
						GroupId = groupId,
						LastServedSequence = nextSequence,
						ConcurrencyStamp = Guid.NewGuid(),
					}, cancellationToken).ConfigureAwait(false);
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
					if (!await UpdateFairQueueGroupAsync(connection, cursor, cursorStamp, cancellationToken)
						.ConfigureAwait(false))
					{
						throw new LostRaceException();
					}
				}
			}

			await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
			return ToRecord(candidate);
		}
		catch (LostRaceException)
		{
			await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			return null;
		}
		catch (DbException)
		{
			try
			{
				await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
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
			).ConfigureAwait(false))
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
		if (groupId is null || !cursorWasMissing && observedCursorStamp is null)
			return false;

		await using var connection = CreateConnection();
		var currentStamp = await FairQueueGroups(connection)
			.Where(group => group.QueueName == queueName && group.GroupId == groupId)
			.Select(static group => (Guid?)group.ConcurrencyStamp)
			.SingleOrDefaultAsync(cancellationToken)
			.ConfigureAwait(false);
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
			.Select(static job => (job.QueueName, GroupId: job.GroupId))
			.Distinct()
			.ToArray();
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
				)
				.ConfigureAwait(false);
			if (hasLiveJobs)
				continue;

			_ = await FairQueueGroups(connection)
				.Where(group => group.QueueName == queueName && group.GroupId == groupId)
				.DeleteAsync(cancellationToken)
				.ConfigureAwait(false);
		}
	}

	private static void ValidateDynamicJob(JobRecord job, string description)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);
		ArgumentException.ThrowIfNullOrWhiteSpace(job.JobName);
		if (job.State is not (JobState.Pending or JobState.Scheduled))
			throw new ImmediateJobException($"{description} '{job.Id}' has invalid state '{job.State}'.");
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
		var now = _timeProvider.GetUtcNow().UtcTicks;
		var ids = jobIds.ToArray();
		await using var connection = CreateConnection();
		var candidates = await Jobs(connection)
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
		long now,
		CancellationToken cancellationToken
	)
	{
		var acquired = new List<JobRecord>(candidates.Count);
		foreach (var candidate in candidates)
		{
			await using var connection = CreateConnection();
			_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var oldStamp = candidate.ConcurrencyStamp;
				candidate.State = JobState.Active;
				candidate.WorkerId = workerId;
				candidate.LeaseExpiresAt = now + lease.Ticks;
				candidate.Attempt++;
				candidate.CompletedAt = null;
				candidate.ExecutionTraceId = null;
				candidate.ExecutionSpanId = null;
				candidate.ExecutionStartedAt = null;
				candidate.ConcurrencyStamp = Guid.NewGuid();
				if (!await UpdateJobAsync(connection, candidate, oldStamp, cancellationToken).ConfigureAwait(false))
				{
					await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
					continue;
				}

				if (candidate.BatchId is { } batchId)
				{
					var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
						.ConfigureAwait(false);
					if (batch is not null && batch.StartedAt is null)
					{
						var batchStamp = batch.ConcurrencyStamp;
						batch.StartedAt = now;
						batch.ConcurrencyStamp = Guid.NewGuid();
						if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken).ConfigureAwait(false))
							throw new LostRaceException();
					}
				}

				await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
				acquired.Add(ToRecord(candidate));
			}
			catch (LostRaceException)
			{
				await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			}
		}

		return acquired;
	}

	/// <inheritdoc />
	public async ValueTask SetExecutionTelemetryAsync(
		string jobId,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		await using var connection = CreateConnection();
		_ = await Jobs(connection)
			.Where(job => job.Id == jobId && job.State == JobState.Active && job.WorkerId == workerId)
			.Set(job => job.ExecutionTraceId, traceId)
			.Set(job => job.ExecutionSpanId, spanId)
			.Set(job => job.ExecutionStartedAt, startedAt.UtcTicks)
			.Set(job => job.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken)
			.ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		string jobId,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
		await using var connection = CreateConnection();
		_ = await Jobs(connection)
			.Where(job => job.Id == jobId && job.State == JobState.Active && job.WorkerId == workerId)
			.Set(job => job.LeaseExpiresAt, _timeProvider.GetUtcNow().UtcTicks + lease.Ticks)
			.Set(job => job.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken)
			.ConfigureAwait(false);
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
	public ValueTask AddBatchJobAsync(
		string currentJobId,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(job);
		if (options == ContinuationOptions.Detached)
			throw new ImmediateJobException("AddToBatchAsync cannot create a detached job.");
		if (options is not (ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations))
			throw new ArgumentOutOfRangeException(nameof(options));
		return RetryConcurrencyAsync(
			connection => AddBatchJobCoreAsync(connection, currentJobId, job, options, cancellationToken),
			cancellationToken
		);
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
	public async ValueTask UpsertRecurringAsync(
		RecurringJobSchedule schedule,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		await using var connection = CreateConnection();
		var existing = await Recurring(connection).SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken)
			.ConfigureAwait(false);
		if (existing is null)
		{
			try
			{
				_ = await InsertAsync(connection, ToEntity(schedule), cancellationToken).ConfigureAwait(false);
				return;
			}
			catch (DbException)
			{
				existing = await Recurring(connection).SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken)
					.ConfigureAwait(false);
				if (existing is null)
					throw;
			}
		}

		if (!schedule.IsCodeDefined && existing.IsCodeDefined)
			throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules.");
		var oldStamp = existing.ConcurrencyStamp;
		existing.JobName = schedule.JobName;
		existing.Cron = schedule.Cron;
		existing.TimeZone = schedule.TimeZone;
		existing.IsCodeDefined = schedule.IsCodeDefined;
		existing.NextRunAt = schedule.NextRunAt.UtcTicks;
		existing.ConcurrencyStamp = Guid.NewGuid();
		_ = await UpdateRecurringAsync(connection, existing, oldStamp, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveObsoleteCodeDefinedRecurringAsync(
		IReadOnlyCollection<string> activeScheduleNames,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(activeScheduleNames);
		await using var connection = CreateConnection();
		var schedules = Recurring(connection).Where(schedule => schedule.IsCodeDefined);
		if (activeScheduleNames.Count != 0)
			schedules = schedules.Where(schedule => !activeScheduleNames.Contains(schedule.Name));
		_ = await schedules.DeleteAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		await using var connection = CreateConnection();
		var removed = await Recurring(connection)
			.Where(schedule => schedule.Name == name && !schedule.IsCodeDefined)
			.DeleteAsync(cancellationToken)
			.ConfigureAwait(false);
		if (removed != 0)
			return;
		if (await Recurring(connection).AnyAsync(schedule => schedule.Name == name, cancellationToken).ConfigureAwait(false))
			throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");
		throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
	}

	/// <inheritdoc />
	public ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default) =>
		SetRecurringPausedAsync(name, paused: true, cancellationToken);

	/// <inheritdoc />
	public ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default) =>
		SetRecurringPausedAsync(name, paused: false, cancellationToken);

	private async ValueTask SetRecurringPausedAsync(string name, bool paused, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		await using var connection = CreateConnection();
		_ = await Recurring(connection)
			.Where(schedule => schedule.Name == name)
			.Set(schedule => schedule.IsPaused, paused)
			.Set(schedule => schedule.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken)
			.ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
		await using var connection = CreateConnection();
		var schedules = await Recurring(connection)
			.Where(schedule => !schedule.IsPaused && schedule.NextRunAt <= now.UtcTicks)
			.OrderBy(schedule => schedule.NextRunAt)
			.Take(batchSize)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return [.. schedules.Select(ToRecord)];
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
		await using var connection = CreateConnection();
		_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var entity = await Recurring(connection).SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken)
				.ConfigureAwait(false);
			if (entity is null || entity.IsPaused || entity.NextRunAt != schedule.NextRunAt.UtcTicks)
			{
				await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
				return false;
			}

			var oldStamp = entity.ConcurrencyStamp;
			entity.LastRunAt = schedule.NextRunAt.UtcTicks;
			entity.NextRunAt = nextRunAt.UtcTicks;
			entity.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateRecurringAsync(connection, entity, oldStamp, cancellationToken).ConfigureAwait(false))
				throw new LostRaceException();
			_ = await InsertAsync(connection, ToEntity(job), cancellationToken).ConfigureAwait(false);
			await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (Exception exception) when (exception is LostRaceException or DbException)
		{
			await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			return false;
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(
		CancellationToken cancellationToken = default
	)
	{
		await using var connection = CreateConnection();
		var rawCounts = await Jobs(connection)
			.GroupBy(job => job.State)
			.Select(group => new { State = group.Key, Count = group.LongCount() })
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var counts = Enum.GetValues<JobState>().ToDictionary(static state => state, static _ => 0L);
		foreach (var item in rawCounts)
			counts[item.State] = item.Count;

		var recurringEntities = await Recurring(connection)
			.OrderBy(schedule => schedule.Name)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var serverEntities = await Servers(connection)
			.OrderBy(server => server.WorkerId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return new(
			_timeProvider.GetUtcNow(),
			counts,
			[.. recurringEntities.Select(ToRecord)],
			[.. serverEntities.Select(server => new JobServerSnapshot(
				server.WorkerId,
				FromTicks(server.LastHeartbeat),
				server.ActiveWorkers,
				server.MaxWorkers
			))]
		)
		{
			Capabilities = this.GetCapabilities(),
		};
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Take, 0);
		await using var connection = CreateConnection();
		IQueryable<ImmediateJobEntity> jobs = Jobs(connection);
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
		await using var connection = CreateConnection();
		var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
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
		await using var connection = CreateConnection();
		IQueryable<ImmediateJobBatchEntity> batches = Batches(connection);
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
		await using var connection = CreateConnection();
		IQueryable<ImmediateJobEntity> jobs = Jobs(connection).Where(job => job.BatchId == batchId);
		if (query.State is { } state)
			jobs = jobs.Where(job => job.State == state);
		var entities = await jobs.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return [.. entities.Select(job => new BatchMemberStatus(
			job.Id,
			job.JobName,
			job.QueueName,
			job.State,
			job.Attempt,
			FromTicks(job.CreatedAt),
			FromTicks(job.CompletedAt),
			job.LastError
		))];
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		string batchId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		await using var connection = CreateConnection();
		if (!await Batches(connection).AnyAsync(batch => batch.Id == batchId, cancellationToken).ConfigureAwait(false))
			return null;
		var entities = await Jobs(connection)
			.Where(job => job.BatchId == batchId)
			.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var jobs = entities.Select(job => new BatchGraphNode(job.Id, job.JobName, job.State)).ToArray();
		var ids = jobs.Select(static job => job.JobId).ToArray();
		var edges = ids.Length == 0
			? []
			: await Continuations(connection)
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
		await using var connection = CreateConnection();
		var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
			return null;
		var edges = await Continuations(connection)
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
			MaxAttempts: null,
			FromTicks(job.CreatedAt),
			FromTicks(job.DueAt),
			FromTicks(job.CompletedAt),
			job.LastError,
			job.BatchId,
			[.. edges.Select(ToGraphEdge)]
		);
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		var terminalGroups = new HashSet<(string QueueName, string GroupId)>();
		await RetryConcurrencyAsync(
			connection => CancelBatchCoreAsync(
				connection,
				batchId,
				terminalGroups,
				cancellationToken
			),
			cancellationToken
		).ConfigureAwait(false);
		await CleanupFairQueueGroupsAsync(terminalGroups).ConfigureAwait(false);
	}

	private async Task CancelBatchCoreAsync(
		DataConnection connection,
		string batchId,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var now = _timeProvider.GetUtcNow().UtcTicks;
		var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
			.ConfigureAwait(false)
			?? throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
		if (batch.State != BatchState.Executing)
			throw new ImmediateJobException("Only an executing batch can be cancelled.");
		var jobs = await Jobs(connection).Where(job => job.BatchId == batchId).ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		foreach (var job in jobs)
		{
			if (IsTerminal(job.State))
				continue;
			var oldStamp = job.ConcurrencyStamp;
			job.State = JobState.Cancelled;
			job.CompletedAt = now;
			job.WorkerId = null;
			job.LeaseExpiresAt = null;
			job.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken).ConfigureAwait(false))
				throw new LostRaceException();
			await PropagateTerminalAsync(
				connection,
				job,
				now,
				terminalGroups,
				cancellationToken
			).ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
		await using var connection = CreateConnection();
		_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
				.ConfigureAwait(false)
				?? throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
			if (batch.State == BatchState.Executing)
				throw new ImmediateJobException("Only a terminal batch can be deleted.");
			var jobIds = await Jobs(connection).Where(job => job.BatchId == batchId).Select(job => job.Id)
				.ToArrayAsync(cancellationToken).ConfigureAwait(false);
			_ = await Continuations(connection)
				.Where(edge => jobIds.Contains(edge.ChildJobId)
					|| edge.ParentKind == ContinuationParentKind.Job && jobIds.Contains(edge.ParentId)
					|| edge.ParentKind == ContinuationParentKind.Batch && edge.ParentId == batchId)
				.DeleteAsync(cancellationToken)
				.ConfigureAwait(false);
			_ = await Jobs(connection).Where(job => job.BatchId == batchId).DeleteAsync(cancellationToken).ConfigureAwait(false);
			_ = await Batches(connection).Where(item => item.Id == batchId).DeleteAsync(cancellationToken).ConfigureAwait(false);
			await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			throw;
		}
	}

	/// <inheritdoc />
	public ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		return RetryConcurrencyAsync(
			connection => RetryCoreAsync(connection, jobId, cancellationToken),
			cancellationToken
		);
	}

	private async Task RetryCoreAsync(DataConnection connection, string jobId, CancellationToken cancellationToken)
	{
		var job = await Jobs(connection)
			.SingleOrDefaultAsync(item => item.Id == jobId && item.State == JobState.Failed, cancellationToken)
			.ConfigureAwait(false);
		if (job is null)
		{
			if (await Jobs(connection).AnyAsync(item => item.Id == jobId, cancellationToken).ConfigureAwait(false))
				throw new ImmediateJobException("Only failed jobs can be retried.");
			throw new KeyNotFoundException($"Job '{jobId}' was not found.");
		}

		if (job.BatchId is { } batchId)
		{
			var batch = await Batches(connection).SingleAsync(item => item.Id == batchId, cancellationToken).ConfigureAwait(false);
			var batchStamp = batch.ConcurrencyStamp;
			batch.PendingCount++;
			batch.FailedCount = Math.Max(0, batch.FailedCount - 1);
			batch.State = BatchState.Executing;
			batch.CompletedAt = null;
			batch.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken).ConfigureAwait(false))
				throw new LostRaceException();
		}

		var oldStamp = job.ConcurrencyStamp;
		job.State = JobState.Pending;
		job.DueAt = _timeProvider.GetUtcNow().UtcTicks;
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.CompletedAt = null;
		job.LastError = null;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken).ConfigureAwait(false))
			throw new LostRaceException();
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
		await using var connection = CreateConnection();
		_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobId &&
				(item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled), cancellationToken)
				.ConfigureAwait(false);
			if (job is null)
			{
				if (await Jobs(connection).AnyAsync(item => item.Id == jobId, cancellationToken).ConfigureAwait(false))
					throw new ImmediateJobException("Only terminal jobs can be deleted.");
				throw new KeyNotFoundException($"Job '{jobId}' was not found.");
			}

			if (job.BatchId is not null)
				throw new ImmediateJobException("Batch members are deleted with their batch so the workflow remains coherent.");
			_ = await Continuations(connection)
				.Where(edge => edge.ChildJobId == jobId ||
					(edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == jobId))
				.DeleteAsync(cancellationToken)
				.ConfigureAwait(false);
			var removed = await Jobs(connection)
				.Where(item => item.Id == jobId &&
					(item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled))
				.DeleteAsync(cancellationToken)
				.ConfigureAwait(false);
			if (removed == 0)
			{
				if (await Jobs(connection).AnyAsync(item => item.Id == jobId, cancellationToken).ConfigureAwait(false))
					throw new ImmediateJobException("Only terminal jobs can be deleted.");
				throw new KeyNotFoundException($"Job '{jobId}' was not found.");
			}

			await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			throw;
		}
	}

	/// <inheritdoc />
	public async ValueTask PurgeJobsAsync(
		TimeSpan succeededRetention,
		TimeSpan failedRetention,
		CancellationToken cancellationToken = default
	)
	{
		var now = _timeProvider.GetUtcNow();
		await using var connection = CreateConnection();
		_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var jobIds = await Jobs(connection)
				.Where(job => job.BatchId == null &&
					(job.State == JobState.Succeeded && job.CompletedAt < (now - succeededRetention).UtcTicks
						|| (job.State == JobState.Failed || job.State == JobState.Cancelled)
							&& job.CompletedAt < (now - failedRetention).UtcTicks))
				.Select(job => job.Id)
				.ToArrayAsync(cancellationToken)
				.ConfigureAwait(false);
			if (jobIds.Length != 0)
			{
				_ = await Continuations(connection)
					.Where(edge => jobIds.Contains(edge.ChildJobId)
						|| jobIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
					.DeleteAsync(cancellationToken).ConfigureAwait(false);
				_ = await Jobs(connection).Where(job => jobIds.Contains(job.Id)).DeleteAsync(cancellationToken)
					.ConfigureAwait(false);
			}

			await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			throw;
		}
	}

	/// <inheritdoc />
	public async ValueTask PurgeBatchesAsync(
		TimeSpan batchSucceededRetention,
		TimeSpan batchFailedRetention,
		CancellationToken cancellationToken = default
	)
	{
		var now = _timeProvider.GetUtcNow();
		await using var connection = CreateConnection();
		_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var batchIds = await Batches(connection)
				.Where(batch => batch.State == BatchState.Succeeded && batch.CompletedAt < (now - batchSucceededRetention).UtcTicks
					|| (batch.State == BatchState.Failed || batch.State == BatchState.Cancelled)
						&& batch.CompletedAt < (now - batchFailedRetention).UtcTicks)
				.Select(batch => batch.Id)
				.ToArrayAsync(cancellationToken)
				.ConfigureAwait(false);
			if (batchIds.Length != 0)
			{
				var memberIds = await Jobs(connection).Where(job => job.BatchId != null && batchIds.Contains(job.BatchId))
					.Select(job => job.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
				_ = await Continuations(connection)
					.Where(edge => batchIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Batch
						|| memberIds.Contains(edge.ChildJobId)
						|| memberIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
					.DeleteAsync(cancellationToken).ConfigureAwait(false);
				_ = await Jobs(connection).Where(job => job.BatchId != null && batchIds.Contains(job.BatchId))
					.DeleteAsync(cancellationToken).ConfigureAwait(false);
				_ = await Batches(connection).Where(batch => batchIds.Contains(batch.Id)).DeleteAsync(cancellationToken)
					.ConfigureAwait(false);
			}

			await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			throw;
		}
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(server);
		await using var connection = CreateConnection();
		var updated = await Servers(connection)
			.Where(entity => entity.WorkerId == server.WorkerId)
			.Set(entity => entity.LastHeartbeat, server.LastHeartbeat.UtcTicks)
			.Set(entity => entity.ActiveWorkers, server.ActiveWorkers)
			.Set(entity => entity.MaxWorkers, server.MaxWorkers)
			.UpdateAsync(cancellationToken)
			.ConfigureAwait(false);
		if (updated != 0)
			return;
		try
		{
			_ = await InsertAsync(connection, new ImmediateJobServerEntity
			{
				WorkerId = server.WorkerId,
				LastHeartbeat = server.LastHeartbeat.UtcTicks,
				ActiveWorkers = server.ActiveWorkers,
				MaxWorkers = server.MaxWorkers,
			}, cancellationToken).ConfigureAwait(false);
		}
		catch (DbException)
		{
			_ = await Servers(connection)
				.Where(entity => entity.WorkerId == server.WorkerId)
				.Set(entity => entity.LastHeartbeat, server.LastHeartbeat.UtcTicks)
				.Set(entity => entity.ActiveWorkers, server.ActiveWorkers)
				.Set(entity => entity.MaxWorkers, server.MaxWorkers)
				.UpdateAsync(cancellationToken)
				.ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			await using var connection = CreateConnection();
			_ = await connection.ExecuteAsync("SELECT 1", cancellationToken).ConfigureAwait(false);
			return true;
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
		var terminalGroups = new HashSet<(string QueueName, string GroupId)>();
		await RetryConcurrencyAsync(
			connection => MutateOwnedCoreAsync(
				connection,
				jobId,
				workerId,
				error,
				nextRetryAt,
				succeeded,
				additions,
				terminalGroups,
				cancellationToken
			),
			cancellationToken
		).ConfigureAwait(false);
		await CleanupFairQueueGroupsAsync(terminalGroups).ConfigureAwait(false);
	}

	private async Task CleanupFairQueueGroupsAsync(
		IEnumerable<(string QueueName, string GroupId)> groups
	)
	{
		foreach (var (queueName, groupId) in groups.Distinct())
		{
			try
			{
				await using var connection = CreateConnection();
				if (await Jobs(connection)
					.AnyAsync(
						item => item.QueueName == queueName
							&& item.GroupId == groupId
							&& (item.State == JobState.Pending
								|| item.State == JobState.Scheduled
								|| item.State == JobState.Active),
						CancellationToken.None
					)
					.ConfigureAwait(false))
				{
					continue;
				}

				_ = await FairQueueGroups(connection)
					.Where(group => group.QueueName == queueName && group.GroupId == groupId)
					.DeleteAsync(CancellationToken.None)
					.ConfigureAwait(false);
			}
#pragma warning disable CA1031 // Cleanup cannot make an already committed job transition appear to fail.
			catch (Exception)
#pragma warning restore CA1031
			{
				// Cleanup is best-effort metadata maintenance and must not invalidate a committed transition.
			}
		}
	}

	private async Task MutateOwnedCoreAsync(
		DataConnection connection,
		string jobId,
		string workerId,
		string? error,
		DateTimeOffset? nextRetryAt,
		bool succeeded,
		IReadOnlyList<JobContinuationAddition> additions,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var job = await Jobs(connection)
			.SingleOrDefaultAsync(
				item => item.Id == jobId && item.State == JobState.Active && item.WorkerId == workerId,
				cancellationToken
			)
			.ConfigureAwait(false);
		if (job is null)
			return;

		var oldStamp = job.ConcurrencyStamp;
		var now = _timeProvider.GetUtcNow().UtcTicks;
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.LastError = error;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!succeeded && nextRetryAt is { } retryAt)
		{
			job.State = retryAt.UtcTicks <= now ? JobState.Pending : JobState.Scheduled;
			job.DueAt = retryAt.UtcTicks;
			job.CompletedAt = null;
			if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken).ConfigureAwait(false))
				throw new LostRaceException();
			return;
		}

		if (succeeded && additions.Count != 0)
			await FlushContinuationAdditionsAsync(connection, job, additions, cancellationToken).ConfigureAwait(false);
		job.State = succeeded ? JobState.Succeeded : JobState.Failed;
		job.CompletedAt = now;
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken).ConfigureAwait(false))
			throw new LostRaceException();
		await PropagateTerminalAsync(
			connection,
			job,
			now,
			terminalGroups,
			cancellationToken
		).ConfigureAwait(false);
	}

	private async Task AddBatchJobCoreAsync(
		DataConnection connection,
		string currentJobId,
		JobRecord record,
		ContinuationOptions options,
		CancellationToken cancellationToken
	)
	{
		var current = await Jobs(connection)
			.SingleOrDefaultAsync(job => job.Id == currentJobId && job.State == JobState.Active, cancellationToken)
			.ConfigureAwait(false)
			?? throw new ImmediateJobException($"The current active job '{currentJobId}' was not found.");
		if (current.BatchId is not { } batchId)
			throw new ImmediateJobException("The current job does not belong to a batch.");
		ValidateDynamicJob(record, "Concurrent batch member");
		if (record.BatchId != batchId)
			throw new ImmediateJobException("The new job must belong to the current job's batch.");
		var batch = await Batches(connection)
			.SingleAsync(item => item.Id == batchId && item.State == BatchState.Executing, cancellationToken)
			.ConfigureAwait(false);
		await ResetReturningGroupCursorsAsync(connection, [record], cancellationToken).ConfigureAwait(false);
		var job = ToEntity(record);
		_ = await InsertAsync(connection, job, cancellationToken).ConfigureAwait(false);
		var batchStamp = batch.ConcurrencyStamp;
		batch.TotalJobs++;
		batch.PendingCount++;
		batch.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken).ConfigureAwait(false))
			throw new LostRaceException();

		if (options != ContinuationOptions.BeforeContinuations)
			return;
		var waiters = await GetActiveWaitersAsync(connection, currentJobId, cancellationToken).ConfigureAwait(false);
		foreach (var waiter in waiters)
		{
			_ = await InsertAsync(connection, new ImmediateJobContinuationEntity
			{
				ChildJobId = waiter.Id,
				ParentKind = ContinuationParentKind.Job,
				ParentId = job.Id,
				Trigger = ContinuationTrigger.Success,
			}, cancellationToken).ConfigureAwait(false);
			var waiterStamp = waiter.ConcurrencyStamp;
			waiter.RemainingDependencies++;
			waiter.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, waiter, waiterStamp, cancellationToken).ConfigureAwait(false))
				throw new LostRaceException();
		}
	}

	private async Task FlushContinuationAdditionsAsync(
		DataConnection connection,
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
			ValidateDynamicJob(addition.Job, "Dynamic continuation");
			if (!ids.Add(addition.Job.Id))
				throw new ImmediateJobException($"Job '{addition.Job.Id}' occurs more than once in the completion buffer.");
			if (!Enum.IsDefined(addition.Trigger))
				throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation trigger.");

			if (addition.Options == ContinuationOptions.Detached)
			{
				if (addition.Job.BatchId is not null)
					throw new ImmediateJobException("A detached continuation cannot belong to a batch.");
			}
			else if (addition.Options is ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations)
			{
				if (current.BatchId is null || addition.Job.BatchId != current.BatchId)
					throw new ImmediateJobException("A batch-tracked continuation must belong to the current job's batch.");
				trackedAdditions++;
			}
			else
			{
				throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation option.");
			}
		}

		var waiters = additions.Any(static addition => addition.Options == ContinuationOptions.BeforeContinuations)
			? await GetActiveWaitersAsync(connection, current.Id, cancellationToken).ConfigureAwait(false)
			: [];
		if (trackedAdditions != 0)
		{
			if (current.BatchId is not { } batchId)
				throw new ImmediateJobException("The current job does not belong to a batch.");
			var batch = await Batches(connection)
				.SingleAsync(item => item.Id == batchId && item.State == BatchState.Executing, cancellationToken)
				.ConfigureAwait(false);
			var batchStamp = batch.ConcurrencyStamp;
			batch.TotalJobs += trackedAdditions;
			batch.PendingCount += trackedAdditions;
			batch.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken).ConfigureAwait(false))
				throw new LostRaceException();
		}

		await ResetReturningGroupCursorsAsync(
			connection,
			[.. additions.Select(static addition => addition.Job)],
			cancellationToken
		).ConfigureAwait(false);

		foreach (var addition in additions)
		{
			var job = ToEntity(addition.Job with
			{
				State = JobState.AwaitingContinuation,
				RemainingDependencies = 1,
			});
			_ = await InsertAsync(connection, job, cancellationToken).ConfigureAwait(false);
			_ = await InsertAsync(connection, new ImmediateJobContinuationEntity
			{
				ChildJobId = job.Id,
				ParentKind = ContinuationParentKind.Job,
				ParentId = current.Id,
				Trigger = addition.Trigger,
			}, cancellationToken).ConfigureAwait(false);

			if (addition.Options != ContinuationOptions.BeforeContinuations)
				continue;
			foreach (var waiter in waiters)
			{
				_ = await InsertAsync(connection, new ImmediateJobContinuationEntity
				{
					ChildJobId = waiter.Id,
					ParentKind = ContinuationParentKind.Job,
					ParentId = job.Id,
					Trigger = ContinuationTrigger.Success,
				}, cancellationToken).ConfigureAwait(false);
				var waiterStamp = waiter.ConcurrencyStamp;
				waiter.RemainingDependencies++;
				waiter.ConcurrencyStamp = Guid.NewGuid();
				if (!await UpdateJobAsync(connection, waiter, waiterStamp, cancellationToken).ConfigureAwait(false))
					throw new LostRaceException();
			}
		}
	}

	private async Task<List<ImmediateJobEntity>> GetActiveWaitersAsync(
		DataConnection connection,
		string currentJobId,
		CancellationToken cancellationToken
	)
	{
		var waiterIds = await Continuations(connection)
			.Where(edge => edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == currentJobId)
			.Select(edge => edge.ChildJobId)
			.Distinct()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);
		return waiterIds.Length == 0
			? []
			: await Jobs(connection)
				.Where(job => waiterIds.Contains(job.Id) && job.State == JobState.AwaitingContinuation)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
	}

	private async Task PropagateTerminalAsync(
		DataConnection connection,
		ImmediateJobEntity terminalJob,
		long now,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		AddFairQueueGroup(terminalGroups, terminalJob);
		var parents = new Queue<(ContinuationParentKind Kind, string Id, bool Succeeded, bool Failed)>();
		var processed = new HashSet<(ContinuationParentKind Kind, string Id)>();
		parents.Enqueue((
			ContinuationParentKind.Job,
			terminalJob.Id,
			terminalJob.State == JobState.Succeeded,
			terminalJob.State == JobState.Failed
		));
		await UpdateBatchForTerminalJobAsync(connection, terminalJob, now, parents, cancellationToken)
			.ConfigureAwait(false);

		while (parents.TryDequeue(out var parent))
		{
			if (!processed.Add((parent.Kind, parent.Id)))
				continue;
			var edges = await Continuations(connection)
				.Where(edge => edge.ParentKind == parent.Kind && edge.ParentId == parent.Id)
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			foreach (var edge in edges)
			{
				var child = await Jobs(connection).SingleOrDefaultAsync(job => job.Id == edge.ChildJobId, cancellationToken)
					.ConfigureAwait(false);
				if (child is null || IsTerminal(child.State))
					continue;
				var childStamp = child.ConcurrencyStamp;
				if (child.State != JobState.AwaitingContinuation || child.RemainingDependencies <= 0)
					continue;
				child.RemainingDependencies--;
				if (parent.Failed)
					child.FailedDependencies++;
				if (child.RemainingDependencies == 0)
				{
					if (await ShouldCancelSettledContinuationAsync(connection, child.Id, cancellationToken).ConfigureAwait(false))
					{
						child.State = JobState.Cancelled;
						child.CompletedAt = now;
						child.WorkerId = null;
						child.LeaseExpiresAt = null;
						AddFairQueueGroup(terminalGroups, child);
						parents.Enqueue((ContinuationParentKind.Job, child.Id, Succeeded: false, Failed: false));
						await UpdateBatchForTerminalJobAsync(connection, child, now, parents, cancellationToken)
							.ConfigureAwait(false);
					}
					else
					{
						child.State = child.DueAt <= now ? JobState.Pending : JobState.Scheduled;
					}
				}

				child.ConcurrencyStamp = Guid.NewGuid();
				if (!await UpdateJobAsync(connection, child, childStamp, cancellationToken).ConfigureAwait(false))
					throw new LostRaceException();
			}
		}
	}

	private async Task<bool> ShouldCancelSettledContinuationAsync(
		DataConnection connection,
		string childJobId,
		CancellationToken cancellationToken
	)
	{
		var edges = await Continuations(connection)
			.Where(edge => edge.ChildJobId == childJobId)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var jobParentIds = edges
			.Where(static edge => edge.ParentKind == ContinuationParentKind.Job)
			.Select(static edge => edge.ParentId)
			.Distinct()
			.ToArray();
		var batchParentIds = edges
			.Where(static edge => edge.ParentKind == ContinuationParentKind.Batch)
			.Select(static edge => edge.ParentId)
			.Distinct()
			.ToArray();
		var jobStates = jobParentIds.Length == 0
			? []
			: await Jobs(connection)
				.Where(job => jobParentIds.Contains(job.Id))
				.Select(static job => new { job.Id, job.State })
				.ToDictionaryAsync(static job => job.Id, static job => job.State, cancellationToken)
				.ConfigureAwait(false);
		var batchStates = batchParentIds.Length == 0
			? []
			: await Batches(connection)
				.Where(batch => batchParentIds.Contains(batch.Id))
				.Select(static batch => new { batch.Id, batch.State })
				.ToDictionaryAsync(static batch => batch.Id, static batch => batch.State, cancellationToken)
				.ConfigureAwait(false);

		var requiresFailure = false;
		var anyParentFailed = false;
		// A missing parent has no success or failure outcome; Complete continuations can still proceed.
		foreach (var edge in edges)
		{
			bool succeeded;
			bool failed;
			if (edge.ParentKind == ContinuationParentKind.Job)
			{
				JobState? state = jobStates.TryGetValue(edge.ParentId, out var parentState) ? parentState : null;
				succeeded = state == JobState.Succeeded;
				failed = state == JobState.Failed;
			}
			else
			{
				BatchState? state = batchStates.TryGetValue(edge.ParentId, out var parentState) ? parentState : null;
				succeeded = state == BatchState.Succeeded;
				failed = state == BatchState.Failed;
			}

			if (edge.Trigger == ContinuationTrigger.Success && !succeeded)
				return true;
			requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
			anyParentFailed |= failed;
		}

		return requiresFailure && !anyParentFailed;
	}

	private static void AddFairQueueGroup(
		ISet<(string QueueName, string GroupId)> groups,
		ImmediateJobEntity job
	)
	{
		if (job.GroupId is { } groupId)
			_ = groups.Add((job.QueueName, groupId));
	}

	private async Task UpdateBatchForTerminalJobAsync(
		DataConnection connection,
		ImmediateJobEntity job,
		long now,
		Queue<(ContinuationParentKind Kind, string Id, bool Succeeded, bool Failed)> parents,
		CancellationToken cancellationToken
	)
	{
		if (job.BatchId is not { } batchId)
			return;
		var batch = await Batches(connection).SingleAsync(item => item.Id == batchId, cancellationToken)
			.ConfigureAwait(false);
		var oldStamp = batch.ConcurrencyStamp;
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
		if (batch.PendingCount == 0)
		{
			batch.State = GetTerminalBatchState(batch.FailedCount, batch.CancelledCount);
			batch.CompletedAt = now;
			parents.Enqueue((
				ContinuationParentKind.Batch,
				batch.Id,
				batch.State == BatchState.Succeeded,
				batch.State == BatchState.Failed
			));
		}

		if (!await UpdateBatchAsync(connection, batch, oldStamp, cancellationToken).ConfigureAwait(false))
			throw new LostRaceException();
	}

	private async Task EvaluateInitialDependenciesAsync(
		DataConnection connection,
		Dictionary<string, ImmediateJobEntity> jobs,
		ImmediateJobContinuationEntity[] edges,
		long now,
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
			: (await Jobs(connection).Where(job => externalJobIds.Contains(job.Id)).ToListAsync(cancellationToken)
				.ConfigureAwait(false)).ToDictionary(job => job.Id, job => job.State, StringComparer.Ordinal);
		var externalBatches = externalBatchIds.Length == 0
			? []
			: (await Batches(connection).Where(batch => externalBatchIds.Contains(batch.Id)).ToListAsync(cancellationToken)
				.ConfigureAwait(false)).ToDictionary(batch => batch.Id, batch => batch.State, StringComparer.Ordinal);
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
			throw new ImmediateJobException("The continuation graph contains a dependency cycle.");
	}

	private async ValueTask RetryConcurrencyAsync(
		Func<DataConnection, Task> operation,
		CancellationToken cancellationToken
	)
	{
		for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
		{
			await using var connection = CreateConnection();
			_ = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				await operation(connection).ConfigureAwait(false);
				await connection.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
				return;
			}
			catch (LostRaceException) when (attempt + 1 < MaxConcurrencyAttempts)
			{
				await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				await connection.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
				throw;
			}
		}
	}

	private async Task<bool> UpdateJobAsync(
		DataConnection connection,
		ImmediateJobEntity job,
		Guid oldStamp,
		CancellationToken cancellationToken
	)
	{
		var updated = await Jobs(connection)
			.Where(entity => entity.Id == job.Id && entity.ConcurrencyStamp == oldStamp)
			.Set(entity => entity.QueueName, job.QueueName)
			.Set(entity => entity.JobName, job.JobName)
			.Set(entity => entity.GroupId, job.GroupId)
			.Set(entity => entity.Payload, job.Payload)
			.Set(entity => entity.Context, job.Context)
			.Set(entity => entity.State, job.State)
			.Set(entity => entity.DueAt, job.DueAt)
			.Set(entity => entity.CreatedAt, job.CreatedAt)
			.Set(entity => entity.Attempt, job.Attempt)
			.Set(entity => entity.WorkerId, job.WorkerId)
			.Set(entity => entity.LeaseExpiresAt, job.LeaseExpiresAt)
			.Set(entity => entity.LastError, job.LastError)
			.Set(entity => entity.CompletedAt, job.CompletedAt)
			.Set(entity => entity.RecurringKey, job.RecurringKey)
			.Set(entity => entity.TraceParent, job.TraceParent)
			.Set(entity => entity.TraceState, job.TraceState)
			.Set(entity => entity.ExecutionTraceId, job.ExecutionTraceId)
			.Set(entity => entity.ExecutionSpanId, job.ExecutionSpanId)
			.Set(entity => entity.ExecutionStartedAt, job.ExecutionStartedAt)
			.Set(entity => entity.BatchId, job.BatchId)
			.Set(entity => entity.RemainingDependencies, job.RemainingDependencies)
			.Set(entity => entity.FailedDependencies, job.FailedDependencies)
			.Set(entity => entity.ConcurrencyStamp, job.ConcurrencyStamp)
			.UpdateAsync(cancellationToken)
			.ConfigureAwait(false);
		return updated != 0;
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
			.UpdateAsync(cancellationToken)
			.ConfigureAwait(false);
		return updated != 0;
	}

	private async Task<bool> UpdateBatchAsync(
		DataConnection connection,
		ImmediateJobBatchEntity batch,
		Guid oldStamp,
		CancellationToken cancellationToken
	)
	{
		var updated = await Batches(connection)
			.Where(entity => entity.Id == batch.Id && entity.ConcurrencyStamp == oldStamp)
			.Set(entity => entity.CreatedAt, batch.CreatedAt)
			.Set(entity => entity.TotalJobs, batch.TotalJobs)
			.Set(entity => entity.PendingCount, batch.PendingCount)
			.Set(entity => entity.SucceededCount, batch.SucceededCount)
			.Set(entity => entity.FailedCount, batch.FailedCount)
			.Set(entity => entity.CancelledCount, batch.CancelledCount)
			.Set(entity => entity.StartedAt, batch.StartedAt)
			.Set(entity => entity.CompletedAt, batch.CompletedAt)
			.Set(entity => entity.State, batch.State)
			.Set(entity => entity.ConcurrencyStamp, batch.ConcurrencyStamp)
			.UpdateAsync(cancellationToken)
			.ConfigureAwait(false);
		return updated != 0;
	}

	private async Task<bool> UpdateRecurringAsync(
		DataConnection connection,
		ImmediateRecurringJobEntity schedule,
		Guid oldStamp,
		CancellationToken cancellationToken
	)
	{
		var updated = await Recurring(connection)
			.Where(entity => entity.Name == schedule.Name && entity.ConcurrencyStamp == oldStamp)
			.Set(entity => entity.JobName, schedule.JobName)
			.Set(entity => entity.Cron, schedule.Cron)
			.Set(entity => entity.TimeZone, schedule.TimeZone)
			.Set(entity => entity.IsCodeDefined, schedule.IsCodeDefined)
			.Set(entity => entity.IsPaused, schedule.IsPaused)
			.Set(entity => entity.NextRunAt, schedule.NextRunAt)
			.Set(entity => entity.LastRunAt, schedule.LastRunAt)
			.Set(entity => entity.ConcurrencyStamp, schedule.ConcurrencyStamp)
			.UpdateAsync(cancellationToken)
			.ConfigureAwait(false);
		return updated != 0;
	}

	private DataConnection CreateConnection() => new(_dataOptions);

	private ITable<ImmediateJobEntity> Jobs(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobEntity>());

	private ITable<ImmediateJobBatchEntity> Batches(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobBatchEntity>());

	private ITable<ImmediateFairQueueGroupEntity> FairQueueGroups(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateFairQueueGroupEntity>());

	private ITable<ImmediateJobContinuationEntity> Continuations(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobContinuationEntity>());

	private ITable<ImmediateRecurringJobEntity> Recurring(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateRecurringJobEntity>());

	private ITable<ImmediateJobServerEntity> Servers(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobServerEntity>());

	private ITable<T> WithSchema<T>(ITable<T> table)
		where T : notnull => _schema is null ? table : table.SchemaName(_schema);

	private Task<int> InsertAsync<T>(DataConnection connection, T entity, CancellationToken cancellationToken)
		where T : notnull => connection.InsertAsync(entity, schemaName: _schema, token: cancellationToken);

	private static bool IsTerminal(JobState state) =>
		state is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

	private static BatchState GetTerminalBatchState(int failed, int cancelled)
	{
		if (failed != 0)
			return BatchState.Failed;
		return cancelled != 0 ? BatchState.Cancelled : BatchState.Succeeded;
	}

	private static long? Ticks(DateTimeOffset? value) => value?.UtcTicks;

	private static DateTimeOffset FromTicks(long value) => new(value, TimeSpan.Zero);

	private static DateTimeOffset? FromTicks(long? value) =>
		value is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

	private static ImmediateJobEntity ToEntity(JobRecord job) => new()
	{
		Id = job.Id,
		QueueName = job.QueueName,
		JobName = job.JobName,
		Payload = job.Payload,
		Context = job.Context,
		GroupId = job.GroupId,
		State = job.State,
		DueAt = job.DueAt.UtcTicks,
		CreatedAt = job.CreatedAt.UtcTicks,
		Attempt = job.Attempt,
		WorkerId = job.WorkerId,
		LeaseExpiresAt = Ticks(job.LeaseExpiresAt),
		LastError = job.LastError,
		CompletedAt = Ticks(job.CompletedAt),
		RecurringKey = job.RecurringKey,
		TraceParent = job.TraceParent,
		TraceState = job.TraceState,
		ExecutionTraceId = job.ExecutionTraceId,
		ExecutionSpanId = job.ExecutionSpanId,
		ExecutionStartedAt = Ticks(job.ExecutionStartedAt),
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

	private static JobRecord ToRecord(ImmediateJobEntity job) => new()
	{
		Id = job.Id,
		QueueName = job.QueueName,
		JobName = job.JobName,
		Payload = job.Payload,
		Context = job.Context,
		GroupId = job.GroupId,
		State = job.State,
		DueAt = FromTicks(job.DueAt),
		CreatedAt = FromTicks(job.CreatedAt),
		Attempt = job.Attempt,
		WorkerId = job.WorkerId,
		LeaseExpiresAt = FromTicks(job.LeaseExpiresAt),
		LastError = job.LastError,
		CompletedAt = FromTicks(job.CompletedAt),
		RecurringKey = job.RecurringKey,
		TraceParent = job.TraceParent,
		TraceState = job.TraceState,
		ExecutionTraceId = job.ExecutionTraceId,
		ExecutionSpanId = job.ExecutionSpanId,
		ExecutionStartedAt = FromTicks(job.ExecutionStartedAt),
		BatchId = job.BatchId,
		RemainingDependencies = job.RemainingDependencies,
		FailedDependencies = job.FailedDependencies,
	};

	private static ImmediateRecurringJobEntity ToEntity(RecurringJobSchedule schedule) => new()
	{
		Name = schedule.Name,
		JobName = schedule.JobName,
		Cron = schedule.Cron,
		TimeZone = schedule.TimeZone,
		IsCodeDefined = schedule.IsCodeDefined,
		IsPaused = schedule.IsPaused,
		NextRunAt = schedule.NextRunAt.UtcTicks,
		LastRunAt = Ticks(schedule.LastRunAt),
		ConcurrencyStamp = Guid.NewGuid(),
	};

	private static RecurringJobSchedule ToRecord(ImmediateRecurringJobEntity schedule) => new()
	{
		Name = schedule.Name,
		JobName = schedule.JobName,
		Cron = schedule.Cron,
		TimeZone = schedule.TimeZone,
		IsCodeDefined = schedule.IsCodeDefined,
		IsPaused = schedule.IsPaused,
		NextRunAt = FromTicks(schedule.NextRunAt),
		LastRunAt = FromTicks(schedule.LastRunAt),
	};

	private static BatchStatus ToStatus(ImmediateJobBatchEntity batch) => new(
		batch.Id,
		batch.State,
		batch.TotalJobs,
		batch.SucceededCount,
		batch.FailedCount,
		batch.CancelledCount,
		batch.PendingCount,
		FromTicks(batch.CreatedAt),
		FromTicks(batch.StartedAt),
		FromTicks(batch.CompletedAt),
		BatchStatus.CalculateFractionSettled(batch.TotalJobs, batch.PendingCount)
	);

	private static JobContinuationEdge ToContinuationEdge(ImmediateJobContinuationEntity edge) => new()
	{
		ChildJobId = edge.ChildJobId,
		ParentJobId = edge.ParentKind == ContinuationParentKind.Job ? edge.ParentId : null,
		ParentBatchId = edge.ParentKind == ContinuationParentKind.Batch ? edge.ParentId : null,
		Trigger = edge.Trigger,
	};

	private static BatchGraphEdge ToGraphEdge(ImmediateJobContinuationEntity edge) => new(
		edge.ChildJobId,
		edge.ParentKind == ContinuationParentKind.Job ? edge.ParentId : null,
		edge.ParentKind == ContinuationParentKind.Batch ? edge.ParentId : null,
		edge.Trigger
	);

#pragma warning disable CA1032, CA1064
	private sealed class LostRaceException : Exception;
#pragma warning restore CA1032, CA1064
}

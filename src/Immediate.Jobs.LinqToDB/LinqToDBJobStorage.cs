using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.LinqToDB;

/// <summary>An optimistic-concurrency LinqToDB implementation of <see cref="IJobStorage"/>.</summary>
internal sealed partial class LinqToDBJobStorage<T>(
	Owned<T> contextScope,
	IOptions<LinqToDBJobStorageOptions> options,
	TimeProvider timeProvider,
	ILogger<LinqToDBJobStorage<T>>? logger = null
) : IRecurringJobStorage, IJobGraphStorage, IFairQueueStorage, IJobStorageReplica, IJobGraphStorageReplica
	where T : DataConnection
{
	[SuppressMessage("Performance", "CA1823:Avoid unused private fields", Justification = "Used by generated logger methods")]
	[SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Used by generated logger methods")]
	private readonly ILogger _logger = logger ?? NullLogger<LinqToDBJobStorage<T>>.Instance;

	private const int MaxContendedCompletionAttempts = 50;
	private const int MaxConcurrencyAttempts = 5;
	private const int MaxConsecutiveFailedFairClaims = 5;

	private readonly string? _schema = options.Value.Schema;

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		DisposeAsyncCalled();
		await TaskScheduler.Yield();
		await ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		InitializeAsyncCalled();
		await TaskScheduler.Yield();
		await ValueTask.CompletedTask;
	}

	/// <summary>
	///		Used for testing to pre-load various values to the storage before the test starts.
	/// </summary>
	/// <param name="jobs">
	///		The jobs that should be loaded in the database.
	/// </param>
	/// <param name="batches">
	///		The batches that should be loaded in the database.
	/// </param>
	/// <param name="edges">
	///		The continuation edges that should be loaded in the database.
	/// </param>
	/// <param name="recurringSchedules">
	///		The recurring schedules that should be loaded in the database.
	/// </param>
	/// <remarks>
	///	    This method should run before any other methods run to initialize test state. Use in regular app code is not
	///	    supported.
	/// </remarks>
	public async ValueTask LoadPersistedJobState(
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<BatchRecord> batches,
		IReadOnlyList<JobContinuationEdge> edges,
		IReadOnlyList<RecurringJobSchedule> recurringSchedules
	)
	{
		LoadPersistedJobStateCalled(jobs.Count, edges.Count);
		await using var scope = contextScope.GetScope(out var connection);

		await connection.BulkCopyAsync(
			new BulkCopyOptions { SchemaName = _schema },
			batches.Select(batch => new ImmediateJobBatchEntity
			{
				Id = batch.BatchHandle.Value,
				CreatedAt = batch.CreatedAt,
				TotalJobs = batch.TotalJobs,
				PendingCount = batch.PendingCount,
				SucceededCount = batch.SucceededCount,
				FailedCount = batch.FailedCount,
				CancelledCount = batch.CancelledCount,
				SkippedCount = batch.SkippedCount,
				StartedAt = batch.StartedAt,
				CompletedAt = batch.CompletedAt,
				State = batch.State,
				ConcurrencyStamp = Guid.NewGuid(),
			})
		);

		await connection.BulkCopyAsync(
			new BulkCopyOptions { SchemaName = _schema },
			jobs.Select(ToEntity)
		);

		await connection.BulkCopyAsync(
			new BulkCopyOptions { SchemaName = _schema },
			edges.Select(ToEntity)
		);

		await connection.BulkCopyAsync(
			new BulkCopyOptions { SchemaName = _schema },
			recurringSchedules.Select(ToEntity)
		);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		EnqueueAsyncCalled(job.JobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		_ = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			await ResetReturningGroupCursorsAsync(connection, [job], cancellationToken);
			_ = await InsertAsync(connection, ToEntity(job), cancellationToken);
			await connection.CommitTransactionAsync(cancellationToken);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			throw;
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(
		IReadOnlyCollection<JobHandle> childJobHandles,
		CancellationToken cancellationToken = default
	)
	{
		GetIncomingEdgesAsyncCalled(childJobHandles.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var ids = childJobHandles.Select(i => i.Value).Distinct(StringComparer.Ordinal).ToList();

		await using var scope = contextScope.GetScope(out var connection);

		var edges = await Continuations(connection)
			.Where(edge => ids.Contains(edge.ChildJobHandle))
			.OrderBy(edge => edge.ChildJobHandle)
			.ThenBy(edge => edge.ParentKind)
			.ThenBy(edge => edge.ParentId)
			.ToListAsync(cancellationToken);
		return [.. edges.Select(ToContinuationEdge)];
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		EnqueueContinuationAsyncCalled(job.JobHandle, edges.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await ExecuteGraphInsertAsync(batch: null, [job], edges, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		EnqueueBatchAsyncCalled(batch.BatchHandle, jobs.Count, edges.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await ExecuteGraphInsertAsync(batch, jobs, edges, cancellationToken);
	}

	private ValueTask ExecuteGraphInsertAsync(
		BatchRecord? batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken
	) => RetryConcurrencyAsync(
		connection => InsertGraphCoreAsync(connection, batch, jobs, edges, cancellationToken),
		cancellationToken
	);

	private async Task InsertGraphCoreAsync(
		DataConnection connection,
		BatchRecord? batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken
	)
	{
		var jobHandles = jobs.Select(static job => job.JobHandle.Value).ToHashSet(StringComparer.Ordinal);
		if (jobHandles.Count != jobs.Count)
			throw new ImmediateJobException("A batch or continuation insert contains duplicate job identifiers.");
		if (batch is not null && jobs.Any(job => job.BatchHandle != batch.BatchHandle))
			throw new ImmediateJobException("Every atomic batch member must carry the committed batch identifier.");

		var edgeEntities = edges.Select(ToEntity).ToList();
		if (edgeEntities.Any(edge => !jobHandles.Contains(edge.ChildJobHandle)))
			throw new ImmediateJobException("Every continuation edge must target a job inserted by the same operation.");
		if (edgeEntities.DistinctBy(static edge => (edge.ChildJobHandle, edge.ParentKind, edge.ParentId)).Count() != edgeEntities.Count)
			throw new ImmediateJobException("Duplicate continuation edges are not allowed.");
		ThrowIfCyclic(jobHandles, edgeEntities);

		var jobEntities = jobs.Select(ToEntity).ToDictionary(static job => job.Id, StringComparer.Ordinal);
		await ResetReturningGroupCursorsAsync(connection, jobs, cancellationToken);
		await EvaluateInitialDependenciesAsync(
			connection,
			jobEntities,
			edgeEntities,
			timeProvider.GetUtcNow(),
			cancellationToken
		);

		if (batch is not null)
		{
			var terminal = jobEntities.Values.Where(static job => IsTerminal(job.State)).ToList();
			var pending = jobEntities.Count - terminal.Count;
			var failed = terminal.Count(static job => job.State == JobState.Failed);
			var cancelled = terminal.Count(static job => job.State == JobState.Cancelled);
			var skipped = terminal.Count(static job => job.State == JobState.Skipped);
			_ = await InsertAsync(connection, new ImmediateJobBatchEntity
			{
				Id = batch.BatchHandle.Value,
				CreatedAt = batch.CreatedAt,
				TotalJobs = jobEntities.Count,
				PendingCount = pending,
				SucceededCount = terminal.Count(static job => job.State == JobState.Succeeded),
				FailedCount = failed,
				CancelledCount = cancelled,
				SkippedCount = skipped,
				StartedAt = batch.StartedAt,
				CompletedAt = pending == 0 ? batch.CompletedAt ?? timeProvider.GetUtcNow() : null,
				State = pending == 0 ? GetTerminalBatchState(failed, cancelled) : BatchState.Executing,
				ConcurrencyStamp = Guid.NewGuid(),
			}, cancellationToken);
		}

		foreach (var entity in jobEntities.Values)
			_ = await InsertAsync(connection, entity, cancellationToken);
		foreach (var edge in edgeEntities)
			_ = await InsertAsync(connection, edge, cancellationToken);
	}

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
			var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
			if (queueCapacity <= 0)
				continue;

			var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
			while (queueCapacity > 0)
			{
				var eligibleNames = jobCapacities.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToList();
				if (eligibleNames.Count == 0)
					break;

				await using var scope = contextScope.GetScope(out var readConnection);

				var candidates = await Jobs(readConnection)
					.Where(job => job.QueueName == queue.QueueName && eligibleNames.Contains(job.JobName) &&
						(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
							|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
					.OrderBy(job => job.DueAt)
					.ThenBy(job => job.CreatedAt)
					.ThenBy(job => job.Id)
					.Take(queueCapacity)
					.ToListAsync(cancellationToken);
				if (candidates.Count == 0)
					break;

				var selectionCapacities = new Dictionary<string, int>(jobCapacities, StringComparer.Ordinal);
				var selected = candidates.Where(candidate => selectionCapacities[candidate.JobName]-- > 0).ToList();
				var claimed = await AcquireCandidatesAsync(selected, request.WorkerId, request.Lease, now, cancellationToken);
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

	private static void ValidateDynamicJob(JobRecord job, string description)
	{
		if (job.State is not (JobState.Pending or JobState.Scheduled))
			throw new ImmediateJobException($"{description} '{job.JobHandle}' has invalid state '{job.State}'.");
	}

	private sealed record FairQueueCandidateState(
		string JobHandle,
		int Inflight,
		long LastServedSequence
	);

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

		var candidates = await Jobs(connection)
			.Where(job => job.Id.In(jobHandles.Select(static job => job.Value)) &&
				(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
					|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
			.ToListAsync(cancellationToken);

		return await AcquireCandidatesAsync(candidates, workerId, lease, now, cancellationToken);
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
			await using var scope = contextScope.GetScope(out var connection);

			_ = await connection.BeginTransactionAsync(cancellationToken);
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
				{
					await connection.RollbackTransactionAsync(cancellationToken);
					continue;
				}

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

	/// <inheritdoc />
	public async ValueTask SetExecutionTelemetryAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		SetExecutionTelemetryAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(async connection =>
		{
			var job = await Jobs(connection).SingleOrDefaultAsync(
				item => item.Id == jobHandle.Value && item.Attempt == executionNumber && item.State == JobState.Active && item.WorkerId == workerId,
				cancellationToken
			) ?? throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
			_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken)
				?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
			var oldStamp = job.ConcurrencyStamp;
			job.ExecutionTraceId = traceId;
			job.ExecutionSpanId = spanId;
			job.ExecutionStartedAt = startedAt;
			job.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
				throw new LostRaceException();
			var executionUpdated = await Executions(connection)
				.Where(execution => execution.JobHandle == jobHandle.Value && execution.Attempt == executionNumber && execution.State == JobExecutionState.Active)
				.Set(execution => execution.ExecutionTraceId, traceId)
				.Set(execution => execution.ExecutionSpanId, spanId)
				.Set(execution => execution.ExecutionStartedAt, startedAt)
				.UpdateAsync(cancellationToken);
			if (executionUpdated == 0)
				throw new LostRaceException();
		}, cancellationToken);
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

	/// <inheritdoc />
	public async ValueTask CompleteAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		CancellationToken cancellationToken = default
	)
	{
		CompleteAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, [], cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask CompleteWithContinuationsAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken = default
	)
	{
		CompleteWithContinuationsAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await MutateOwnedWithDependenciesAsync(
			jobHandle,
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
	public async ValueTask AddBatchJobAsync(
		JobHandle currentJobHandle,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		AddBatchJobAsyncCalled(job.JobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(
			connection => AddBatchJobCoreAsync(connection, currentJobHandle, executionNumber, job, options, cancellationToken),
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask FailAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string error,
		DateTimeOffset? nextRetryAt,
		CancellationToken cancellationToken = default
	)
	{
		FailAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await MutateOwnedWithDependenciesAsync(
			jobHandle,
			executionNumber,
			workerId,
			error,
			nextRetryAt,
			succeeded: false,
			[],
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask MergeRecurringSchedulesListAsync(
		IReadOnlyList<RecurringJobSchedule> schedules,
		CancellationToken cancellationToken = default
	)
	{
		MergeRecurringSchedulesListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);
		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

		var existing = await Recurring(connection)
			.ToDictionaryAsync(r => r.Name, StringComparer.Ordinal, cancellationToken);

		foreach (var schedule in schedules)
		{
			var entity = ToEntity(schedule);

			if (!existing.TryGetValue(schedule.Name, out var current))
			{
				await connection.InsertAsync(entity, schemaName: _schema, token: cancellationToken);
				continue;
			}

			existing.Remove(schedule.Name);

			var oldStamp = current.ConcurrencyStamp;

			current.NextRunAt =
				string.Equals(current.Cron, schedule.Cron, StringComparison.Ordinal)
				&& string.Equals(current.TimeZone, schedule.TimeZone, StringComparison.Ordinal)
				? current.NextRunAt
				: schedule.NextRunAt;

			current.JobName = schedule.JobName;
			current.QueueName = schedule.QueueName;
			current.Cron = schedule.Cron;
			current.TimeZone = schedule.TimeZone;
			current.IsCodeDefined = true;
			current.ConcurrencyStamp = Guid.NewGuid();

			if (!await UpdateRecurringAsync(connection, current, oldStamp, cancellationToken))
				throw new ImmediateJobException("Failure saving updated schedule.");
		}

		if (existing.Count != 0)
		{
			var toRemove = existing
				.Where(kvp => kvp.Value.IsCodeDefined)
				.Select(kvp => kvp.Key)
				.ToList();

			await Recurring(connection)
				.Where(r => r.Name.In(toRemove))
				.DeleteAsync(cancellationToken);
		}

		await transaction.CommitAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(
		RecurringJobSchedule schedule,
		CancellationToken cancellationToken = default
	)
	{
		UpsertRecurringAsyncCalled(schedule.Name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(
			Core,
			cancellationToken
		);

		async Task Core(T connection)
		{
			var existing = await Recurring(connection).SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken);
			if (existing is null)
			{
				try
				{
					_ = await InsertAsync(connection, ToEntity(schedule), cancellationToken);
					return;
				}
				catch (DbException)
				{
					existing = await Recurring(connection).SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken);
					if (existing is null)
						throw;
				}
			}

			if (!schedule.IsCodeDefined && existing.IsCodeDefined)
				throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules.");

			var oldStamp = existing.ConcurrencyStamp;
			existing.JobName = schedule.JobName;
			existing.QueueName = schedule.QueueName;
			existing.Cron = schedule.Cron;
			existing.TimeZone = schedule.TimeZone;
			existing.IsCodeDefined = schedule.IsCodeDefined;
			existing.NextRunAt = schedule.NextRunAt;
			existing.ConcurrencyStamp = Guid.NewGuid();

			if (!await UpdateRecurringAsync(connection, existing, oldStamp, cancellationToken))
				throw new LostRaceException();
		}
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		RemoveRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var removed = await Recurring(connection)
			.Where(schedule => schedule.Name == name && !schedule.IsCodeDefined)
			.DeleteAsync(cancellationToken);
		if (removed != 0)
			return;
		if (await Recurring(connection).AnyAsync(schedule => schedule.Name == name, cancellationToken))
			throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");
		throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		PauseRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await SetRecurringPausedAsync(name, paused: true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ResumeRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await SetRecurringPausedAsync(name, paused: false, cancellationToken);
	}

	private async ValueTask SetRecurringPausedAsync(string name, bool paused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		await using var scope = contextScope.GetScope(out var connection);

		var updated = await Recurring(connection)
			.Where(schedule => schedule.Name == name)
			.Set(schedule => schedule.IsPaused, paused)
			.Set(schedule => schedule.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken);
		if (updated == 0)
			throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		GetDueRecurringAsyncCalled(batchSize);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var schedules = await Recurring(connection)
			.Where(schedule => !schedule.IsPaused && schedule.NextRunAt <= now)
			.OrderBy(schedule => schedule.NextRunAt)
			.Take(batchSize)
			.ToListAsync(cancellationToken);
		return [.. schedules.Select(ToRecord)];
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		IReadOnlyList<JobContinuationEdge>? dependencies = null,
		CancellationToken cancellationToken = default
	)
	{
		MaterializeRecurringAsyncCalled(job.JobHandle, schedule.Name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			var entity = await Recurring(connection).SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken);
			if (entity is null || entity.IsPaused || entity.NextRunAt != schedule.NextRunAt)
			{
				return false;
			}

			var oldStamp = entity.ConcurrencyStamp;
			entity.LastRunAt = schedule.NextRunAt;
			entity.NextRunAt = nextRunAt;
			entity.ConcurrencyStamp = Guid.NewGuid();

			if (!await UpdateRecurringAsync(connection, entity, oldStamp, cancellationToken))
				throw new LostRaceException();

			await InsertAsync(connection, ToEntity(job), cancellationToken);

			if (dependencies is { })
			{
				foreach (var d in dependencies)
					await InsertAsync(connection, ToEntity(d), cancellationToken);
			}

			await connection.CommitTransactionAsync(cancellationToken);
			return true;
		}
		catch (Exception exception) when (exception is LostRaceException or DbException)
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			if (exception is DbException && job.RecurringKey is not null)
			{
				await AdvanceRecurringAfterDedupeAsync(
					schedule,
					job.RecurringKey,
					nextRunAt,
					cancellationToken
				);
			}

			return false;
		}
	}

	private async ValueTask AdvanceRecurringAfterDedupeAsync(
		RecurringJobSchedule schedule,
		string recurringKey,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken
	)
	{
		await using var scope = contextScope.GetScope(out var connection);

		if (!await Jobs(connection)
			.AnyAsync(job => job.RecurringKey == recurringKey, cancellationToken))
		{
			return;
		}

		_ = await Recurring(connection)
			.Where(entity =>
				entity.Name == schedule.Name &&
				!entity.IsPaused &&
				entity.NextRunAt == schedule.NextRunAt)
			.Set(entity => entity.LastRunAt, schedule.NextRunAt)
			.Set(entity => entity.NextRunAt, nextRunAt)
			.Set(entity => entity.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(
		CancellationToken cancellationToken = default
	)
	{
		GetMonitoringSnapshotAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var rawCounts = await Jobs(connection)
			.GroupBy(job => job.State)
			.Select(group => new { State = group.Key, Count = group.LongCount() })
			.ToListAsync(cancellationToken);
		var counts = Enum.GetValues<JobState>().ToDictionary(static state => state, static _ => 0L);
		foreach (var item in rawCounts)
			counts[item.State] = item.Count;

		var recurringEntities = await Recurring(connection)
			.OrderBy(schedule => schedule.Name)
			.ToListAsync(cancellationToken);
		var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromMinutes(2);
		var serverEntities = await Servers(connection)
			.Where(server => server.LastHeartbeat >= cutoff)
			.OrderBy(server => server.WorkerId)
			.ToListAsync(cancellationToken);
		return new JobMonitoringSnapshot
		{
			CapturedAt = timeProvider.GetUtcNow(),
			Counts = counts,
			Recurring = [.. recurringEntities.Select(ToRecord)],
			Servers = [.. serverEntities.Select(server => new JobServerSnapshot
			{
				WorkerId = server.WorkerId,
				LastHeartbeat = server.LastHeartbeat,
				ActiveWorkers = server.ActiveWorkers,
				MaxWorkers = server.MaxWorkers,
			})],
			Capabilities = this.GetCapabilities(),
		};
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryJobsAsyncCalled(query);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		IQueryable<ImmediateJobEntity> jobs = Jobs(connection);
		if (query.JobHandle is { Value: { } id })
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
#pragma warning disable CA1304, CA1311, CA1862, MA0011
			jobs = jobs.Where(job => job.JobName.ToUpper().Contains(search));
#pragma warning restore CA1304, CA1311, CA1862, MA0011
		}

		var entities = await jobs.OrderByDescending(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken);
		return [.. entities.Select(ToRecord)];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobHandle,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryJobExecutionsAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var job = await Jobs(connection)
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken);

		if (job is null)
			return [];

		var executions = Executions(connection)
			.Where(execution => execution.JobHandle == jobHandle.Value);

		if (query.Attempt is { } attempt)
			executions = executions.Where(execution => execution.Attempt == attempt);

		var synthetic = JobExecutionRecord.CreateSynthetic(ToRecord(job));
		var syntheticMissing = synthetic is not null
			&& (query.Attempt is null || query.Attempt == synthetic.Attempt)
			&& !await executions.AnyAsync(
				execution => execution.Attempt == synthetic.Attempt,
				cancellationToken
			);
		var skip = query.Skip;
		var take = query.Take;
		var result = new List<JobExecutionRecord>(take);
		if (syntheticMissing && skip == 0)
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
				.ToListAsync(cancellationToken);
			result.AddRange(persisted.Select(ToRecord));
		}

		return result;
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchStatusAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		GetBatchStatusAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken);
		return batch is null ? null : ToStatus(batch);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryBatchesAsyncCalled(query);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		IQueryable<ImmediateJobBatchEntity> batches = Batches(connection);
		if (query.State is { } state)
			batches = batches.Where(batch => batch.State == state);
		var entities = await batches.OrderByDescending(batch => batch.CreatedAt)
			.ThenBy(batch => batch.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken);
		return [.. entities.Select(ToStatus)];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		BatchHandle batchHandle,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryBatchMembersAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var jobs = Jobs(connection).Where(job => job.BatchHandle == batchHandle.Value);
		if (query.State is { } state)
			jobs = jobs.Where(job => job.State == state);

		var entities = await jobs.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken);

		return entities
			.Select(job => new BatchMemberStatus
			{
				JobHandle = JobHandle.FromString(job.Id),
				JobName = job.JobName,
				QueueName = job.QueueName,
				State = job.State,
				Attempt = job.Attempt,
				CreatedAt = job.CreatedAt,
				CompletedAt = job.CompletedAt,
				LastError = job.LastError,
			})
			.ToList();
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		GetBatchGraphAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		if (!await Batches(connection).AnyAsync(batch => batch.Id == batchHandle.Value, cancellationToken))
			return null;
		var entities = await Jobs(connection)
			.Where(job => job.BatchHandle == batchHandle.Value)
			.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.ToListAsync(cancellationToken);
		var jobs = entities.Select(job => new BatchGraphNode { JobHandle = JobHandle.FromString(job.Id), JobName = job.JobName, State = job.State }).ToList();
		var edges = entities.Count == 0
			? []
			: await Continuations(connection)
				.Where(edge => edge.ChildJobHandle.In(entities.Select(static job => job.Id)))
				.OrderBy(edge => edge.ChildJobHandle)
				.ThenBy(edge => edge.ParentKind)
				.ThenBy(edge => edge.ParentId)
				.ToListAsync(cancellationToken);
		return new BatchGraph { BatchHandle = batchHandle, Nodes = jobs, Edges = [.. edges.Select(ToContinuationEdge)] };
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobHandle,
		CancellationToken cancellationToken = default
	)
	{
		GetJobStatusAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken);
		if (job is null)
			return null;
		var edges = await Continuations(connection)
			.Where(edge => edge.ChildJobHandle == jobHandle.Value)
			.OrderBy(edge => edge.ParentKind)
			.ThenBy(edge => edge.ParentId)
			.ToListAsync(cancellationToken);
		return new JobStatus
		{
			JobHandle = JobHandle.FromString(job.Id),
			JobName = job.JobName,
			QueueName = job.QueueName,
			State = job.State,
			Attempt = job.Attempt,
			MaxAttempts = 0,
			CreatedAt = job.CreatedAt,
			DueAt = job.DueAt,
			CompletedAt = job.CompletedAt,
			LastError = job.LastError,
			BatchHandle = BatchHandle.FromString(job.BatchHandle),
			DependsOn = [.. edges.Select(ToContinuationEdge)],
		};
	}

	/// <inheritdoc />
	public async ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		CancelBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var terminalGroups = new HashSet<(string QueueName, string GroupId)>();
		await RetryConcurrencyAsync(
			connection => CancelBatchCoreAsync(
				connection,
				batchHandle,
				terminalGroups,
				cancellationToken
			),
			cancellationToken
		);
		await CleanupFairQueueGroupsAsync(terminalGroups);
	}

	private async Task CancelBatchCoreAsync(
		DataConnection connection,
		BatchHandle batchHandle,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
		if (batch.State != BatchState.Executing)
			throw new ImmediateJobException("Only an executing batch can be cancelled.");
		var jobHandles = await Jobs(connection).Where(job => job.BatchHandle == batchHandle.Value).Select(job => job.Id)
			.ToListAsync(cancellationToken);
		var jobsToCancel = new List<ImmediateJobEntity>(jobHandles.Count);
		foreach (var jobHandle in jobHandles)
		{
			var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle, cancellationToken);
			if (job is null || IsTerminal(job.State))
				continue;
			if (job.State == JobState.Active)
			{
				_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken)
					?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
				_ = await Executions(connection)
					.Where(execution => execution.JobHandle == job.Id && execution.Attempt == job.Attempt)
					.Set(execution => execution.State, JobExecutionState.Cancelled)
					.Set(execution => execution.CompletedAt, now)
					.Set(execution => execution.Error, (string?)null)
						.UpdateAsync(cancellationToken);
			}

			var oldStamp = job.ConcurrencyStamp;
			job.State = JobState.Cancelled;
			job.CompletedAt = now;
			job.WorkerId = null;
			job.LeaseExpiresAt = null;
			job.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
				throw new LostRaceException();
			jobsToCancel.Add(job);
		}

		foreach (var job in jobsToCancel)
		{
			await PropagateTerminalAsync(
				connection,
				job,
				now,
				terminalGroups,
				cancellationToken
			);
		}
	}

	/// <inheritdoc />
	public async ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		DeleteBatchAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		_ = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken)
				?? throw new KeyNotFoundException($"Batch '{batchHandle}' was not found.");
			if (batch.State == BatchState.Executing)
				throw new ImmediateJobException("Only a terminal batch can be deleted.");
			var jobHandles = await Jobs(connection).Where(job => job.BatchHandle == batchHandle.Value).Select(job => job.Id)
				.ToListAsync(cancellationToken);
			_ = await Continuations(connection)
				.Where(edge =>
					jobHandles.Contains(edge.ChildJobHandle)
					|| (edge.ParentKind == ContinuationParentKind.Job && jobHandles.Contains(edge.ParentId))
					|| (edge.ParentKind == ContinuationParentKind.Batch && edge.ParentId == batchHandle.Value)
				)
				.DeleteAsync(cancellationToken);
			_ = await Executions(connection).Where(execution => jobHandles.Contains(execution.JobHandle))
				.DeleteAsync(cancellationToken);
			_ = await Jobs(connection).Where(job => job.BatchHandle == batchHandle.Value).DeleteAsync(cancellationToken);
			_ = await Batches(connection).Where(item => item.Id == batchHandle.Value).DeleteAsync(cancellationToken);
			await connection.CommitTransactionAsync(cancellationToken);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			throw;
		}
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		CancelAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var terminalGroups = new HashSet<(string QueueName, string GroupId)>();
		await RetryConcurrencyAsync(
			connection => CancelCoreAsync(connection, jobHandle, terminalGroups, cancellationToken),
			cancellationToken
		);
		await CleanupFairQueueGroupsAsync(terminalGroups);
	}

	private async Task CancelCoreAsync(
		DataConnection connection,
		JobHandle jobHandle,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken)
			?? throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		if (IsTerminal(job.State))
			throw new ImmediateJobException("Only a non-terminal job can be cancelled.");

		var now = timeProvider.GetUtcNow();
		if (job.State == JobState.Active)
		{
			_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken)
				?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
			_ = await Executions(connection)
				.Where(execution => execution.JobHandle == job.Id && execution.Attempt == job.Attempt)
				.Set(execution => execution.State, JobExecutionState.Cancelled)
				.Set(execution => execution.CompletedAt, now)
				.Set(execution => execution.Error, (string?)null)
				.UpdateAsync(cancellationToken);
		}

		var oldStamp = job.ConcurrencyStamp;
		job.State = JobState.Cancelled;
		job.CompletedAt = now;
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
			throw new LostRaceException();
		await PropagateTerminalAsync(connection, job, now, terminalGroups, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		RetryAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(
			connection => RetryCoreAsync(connection, jobHandle, cancellationToken),
			cancellationToken
		);
	}

	private async Task RetryCoreAsync(DataConnection connection, JobHandle jobHandle, CancellationToken cancellationToken)
	{
		var job = await Jobs(connection)
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value &&
				(item.State == JobState.Failed || item.State == JobState.Scheduled), cancellationToken);
		if (job is null)
		{
			if (await Jobs(connection).AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
				throw new ImmediateJobException("Only failed or scheduled jobs can be retried.");
			throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
		}

		var wasFailed = job.State == JobState.Failed;
		if (wasFailed && job.BatchHandle is { } batchHandle)
		{
			var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle, cancellationToken)
				?? throw new LostRaceException();
			var batchStamp = batch.ConcurrencyStamp;
			batch.PendingCount++;
			batch.FailedCount = Math.Max(0, batch.FailedCount - 1);
			batch.State = BatchState.Executing;
			batch.CompletedAt = null;
			batch.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken))
				throw new LostRaceException();
		}

		_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken);

		var oldStamp = job.ConcurrencyStamp;
		job.State = JobState.Pending;
		job.DueAt = timeProvider.GetUtcNow();
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		if (wasFailed)
		{
			job.CompletedAt = null;
			job.LastError = null;
		}

		job.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
			throw new LostRaceException();
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		DeleteAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		_ = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle.Value &&
				(item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled || item.State == JobState.Skipped), cancellationToken);
			if (job is null)
			{
				if (await Jobs(connection).AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
					throw new ImmediateJobException("Only terminal jobs can be deleted.");
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			}

			if (job.BatchHandle is not null)
				throw new ImmediateJobException("Batch members are deleted with their batch so the workflow remains coherent.");
			_ = await Continuations(connection)
				.Where(edge => edge.ChildJobHandle == jobHandle.Value ||
					(edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == jobHandle.Value))
				.DeleteAsync(cancellationToken);
			_ = await Executions(connection).Where(execution => execution.JobHandle == jobHandle.Value)
				.DeleteAsync(cancellationToken);
			var removed = await Jobs(connection)
				.Where(item => item.Id == jobHandle.Value &&
					(item.State == JobState.Succeeded || item.State == JobState.Failed || item.State == JobState.Cancelled || item.State == JobState.Skipped))
				.DeleteAsync(cancellationToken);
			if (removed == 0)
			{
				if (await Jobs(connection).AnyAsync(item => item.Id == jobHandle.Value, cancellationToken))
					throw new ImmediateJobException("Only terminal jobs can be deleted.");
				throw new KeyNotFoundException($"Job '{jobHandle}' was not found.");
			}

			await connection.CommitTransactionAsync(cancellationToken);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken);
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
		PurgeJobsAsyncCalled(succeededRetention, failedRetention);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var now = timeProvider.GetUtcNow();
		_ = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			var jobHandles = await Jobs(connection)
				.Where(job =>
					job.BatchHandle == null
					&& (
						(
							job.State == JobState.Succeeded
							&& job.CompletedAt < (now - succeededRetention)
						) || (
							(job.State == JobState.Failed || job.State == JobState.Cancelled || job.State == JobState.Skipped)
							&& job.CompletedAt < (now - failedRetention)
						)
					)
				)
				.Select(job => job.Id)
				.ToListAsync(cancellationToken);

			if (jobHandles.Count != 0)
			{
				_ = await Continuations(connection)
					.Where(edge =>
						jobHandles.Contains(edge.ChildJobHandle)
						|| (jobHandles.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
					)
					.DeleteAsync(cancellationToken);
				_ = await Executions(connection).Where(execution => jobHandles.Contains(execution.JobHandle))
					.DeleteAsync(cancellationToken);
				_ = await Jobs(connection).Where(job => jobHandles.Contains(job.Id)).DeleteAsync(cancellationToken);
			}

			await connection.CommitTransactionAsync(cancellationToken);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken);
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
		PurgeBatchesAsyncCalled(batchSucceededRetention, batchFailedRetention);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var now = timeProvider.GetUtcNow();
		await RetryConcurrencyAsync(
			connection => PurgeBatchesCoreAsync(
				connection,
				now - batchSucceededRetention,
				now - batchFailedRetention,
				cancellationToken
			),
			cancellationToken
		);
	}

	private async Task PurgeBatchesCoreAsync(
		DataConnection connection,
		DateTimeOffset batchSucceededBefore,
		DateTimeOffset batchFailedBefore,
		CancellationToken cancellationToken
	)
	{
		var batches = await Batches(connection)
			.Where(batch =>
				(
					batch.State == BatchState.Succeeded
					&& batch.CompletedAt < batchSucceededBefore
				) || (
					(batch.State == BatchState.Failed || batch.State == BatchState.Cancelled)
					&& batch.CompletedAt < batchFailedBefore
				)
			)
			.OrderBy(batch => batch.Id)
			.ToListAsync(cancellationToken);
		if (batches.Count == 0)
			return;

		// Claim batches in ID order before touching dependent rows so retry and purge use the same lock order.
		foreach (var batch in batches)
		{
			var oldStamp = batch.ConcurrencyStamp;
			batch.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateBatchAsync(connection, batch, oldStamp, cancellationToken))
				throw new LostRaceException();
		}

		var batchHandles = batches.Select(static batch => batch.Id).ToList();
		var memberIds = await Jobs(connection)
			.Where(job => job.BatchHandle != null && batchHandles.Contains(job.BatchHandle))
			.Select(job => job.Id)
			.ToListAsync(cancellationToken);
		_ = await Continuations(connection)
			.Where(edge =>
				(batchHandles.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Batch)
				|| memberIds.Contains(edge.ChildJobHandle)
				|| (memberIds.Contains(edge.ParentId) && edge.ParentKind == ContinuationParentKind.Job)
			)
			.DeleteAsync(cancellationToken);
		_ = await Executions(connection).Where(execution => memberIds.Contains(execution.JobHandle))
			.DeleteAsync(cancellationToken);
		_ = await Jobs(connection).Where(job => job.BatchHandle != null && batchHandles.Contains(job.BatchHandle))
			.DeleteAsync(cancellationToken);
		_ = await Batches(connection).Where(batch => batchHandles.Contains(batch.Id)).DeleteAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		HeartbeatAsyncCalled(server.WorkerId);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromMinutes(2);
		_ = await Servers(connection)
			.Where(entity => entity.LastHeartbeat < cutoff)
			.DeleteAsync(cancellationToken);
		var updated = await Servers(connection)
			.Where(entity => entity.WorkerId == server.WorkerId)
			.Set(entity => entity.LastHeartbeat, server.LastHeartbeat)
			.Set(entity => entity.ActiveWorkers, server.ActiveWorkers)
			.Set(entity => entity.MaxWorkers, server.MaxWorkers)
			.UpdateAsync(cancellationToken);
		if (updated != 0)
			return;
		try
		{
			_ = await InsertAsync(connection, new ImmediateJobServerEntity
			{
				WorkerId = server.WorkerId,
				LastHeartbeat = server.LastHeartbeat,
				ActiveWorkers = server.ActiveWorkers,
				MaxWorkers = server.MaxWorkers,
			}, cancellationToken);
		}
		catch (DbException)
		{
			_ = await Servers(connection)
				.Where(entity => entity.WorkerId == server.WorkerId)
				.Set(entity => entity.LastHeartbeat, server.LastHeartbeat)
				.Set(entity => entity.ActiveWorkers, server.ActiveWorkers)
				.Set(entity => entity.MaxWorkers, server.MaxWorkers)
				.UpdateAsync(cancellationToken);
		}
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		IsHealthyAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		try
		{
			await using var scope = contextScope.GetScope(out var connection);

			_ = await connection.ExecuteAsync("SELECT 1", cancellationToken);
			return true;
		}
		catch (Exception exception) when (exception is DbException or InvalidOperationException)
		{
			return false;
		}
	}

	private async ValueTask MutateOwnedWithDependenciesAsync(
		JobHandle jobHandle,
		int executionNumber,
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
				jobHandle,
				executionNumber,
				workerId,
				error,
				nextRetryAt,
				succeeded,
				additions,
				terminalGroups,
				cancellationToken
			),
			cancellationToken,
			maxAttempts: MaxContendedCompletionAttempts
		);
		await CleanupFairQueueGroupsAsync(terminalGroups);
	}

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

	private async Task MutateOwnedCoreAsync(
		DataConnection connection,
		JobHandle jobHandle,
		int executionNumber,
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
				item => item.Id == jobHandle.Value && item.Attempt == executionNumber && item.State == JobState.Active && item.WorkerId == workerId,
				cancellationToken
			) ?? throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
		var oldStamp = job.ConcurrencyStamp;
		var now = timeProvider.GetUtcNow();
		_ = await GetOrMaterializeExecutionAsync(connection, job, cancellationToken)
			?? throw new ImmediateJobException($"Active job '{job.Id}' has no execution ordinal.");
		var executionUpdated = await Executions(connection)
			.Where(execution => execution.JobHandle == jobHandle.Value && execution.Attempt == executionNumber && execution.State == JobExecutionState.Active)
			.Set(execution => execution.State, succeeded ? JobExecutionState.Succeeded : JobExecutionState.Failed)
			.Set(execution => execution.CompletedAt, now)
			.Set(execution => execution.Error, error)
			.UpdateAsync(cancellationToken);
		if (executionUpdated == 0)
			throw new LostRaceException();
		job.WorkerId = null;
		job.LeaseExpiresAt = null;
		job.LastError = error;
		job.ConcurrencyStamp = Guid.NewGuid();
		if (!succeeded && nextRetryAt is { } retryAt)
		{
			job.State = retryAt <= now ? JobState.Pending : JobState.Scheduled;
			job.DueAt = retryAt;
			job.CompletedAt = null;
			if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
				throw new LostRaceException();
			return;
		}

		if (succeeded && additions.Count != 0)
			await FlushContinuationAdditionsAsync(connection, job, additions, cancellationToken);
		job.State = succeeded ? JobState.Succeeded : JobState.Failed;
		job.CompletedAt = now;
		if (!await UpdateJobAsync(connection, job, oldStamp, cancellationToken))
			throw new LostRaceException();
		await PropagateTerminalAsync(
			connection,
			job,
			now,
			terminalGroups,
			cancellationToken
		);
	}

	private async Task AddBatchJobCoreAsync(
		DataConnection connection,
		JobHandle currentJobHandle,
		int executionNumber,
		JobRecord record,
		ContinuationOptions options,
		CancellationToken cancellationToken
	)
	{
		var current = await Jobs(connection)
			.SingleOrDefaultAsync(job => job.Id == currentJobHandle.Value && job.Attempt == executionNumber && job.State == JobState.Active, cancellationToken)
			?? throw new ImmediateJobException($"The current active job '{currentJobHandle}' was not found.");
		if (current.BatchHandle is not { } batchHandle)
			throw new ImmediateJobException("The current job does not belong to a batch.");
		ValidateDynamicJob(record, "Concurrent batch member");
		if (!string.Equals(record.BatchHandle?.Value, batchHandle, StringComparison.Ordinal))
			throw new ImmediateJobException("The new job must belong to the current job's batch.");
		var batch = await Batches(connection)
			.SingleAsync(item => item.Id == batchHandle && item.State == BatchState.Executing, cancellationToken);
		await ResetReturningGroupCursorsAsync(connection, [record], cancellationToken);
		var job = ToEntity(record);
		_ = await InsertAsync(connection, job, cancellationToken);
		var batchStamp = batch.ConcurrencyStamp;
		batch.TotalJobs++;
		batch.PendingCount++;
		batch.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken))
			throw new LostRaceException();

		if (options != ContinuationOptions.BeforeContinuations)
			return;
		var waiters = await GetActiveWaitersAsync(connection, currentJobHandle, cancellationToken);
		foreach (var waiter in waiters)
		{
			_ = await InsertAsync(connection, new ImmediateJobContinuationEntity
			{
				ChildJobHandle = waiter.Id,
				ParentKind = ContinuationParentKind.Job,
				ParentId = job.Id,
				Delay = 0,
				Trigger = ContinuationTrigger.Success,
			}, cancellationToken);
			var waiterStamp = waiter.ConcurrencyStamp;
			waiter.RemainingDependencies++;
			waiter.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, waiter, waiterStamp, cancellationToken))
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
			ValidateDynamicJob(addition.Job, "Dynamic continuation");
			if (!ids.Add(addition.Job.JobHandle.Value))
				throw new ImmediateJobException($"Job '{addition.Job.JobHandle}' occurs more than once in the completion buffer.");
			if (!Enum.IsDefined(addition.Trigger))
				throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation trigger.");

			if (addition.Options == ContinuationOptions.Detached)
			{
				if (addition.Job.BatchHandle is not null)
					throw new ImmediateJobException("A detached continuation cannot belong to a batch.");
			}
			else if (addition.Options is ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations)
			{
				if (current.BatchHandle is null || !string.Equals(addition.Job.BatchHandle?.Value, current.BatchHandle, StringComparison.Ordinal))
					throw new ImmediateJobException("A batch-tracked continuation must belong to the current job's batch.");
				trackedAdditions++;
			}
			else
			{
				throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation option.");
			}
		}

		var waiters = additions.Any(static addition => addition.Options == ContinuationOptions.BeforeContinuations)
			? await GetActiveWaitersAsync(connection, JobHandle.FromString(current.Id), cancellationToken)
			: [];
		if (trackedAdditions != 0)
		{
			if (current.BatchHandle is not { } batchHandle)
				throw new ImmediateJobException("The current job does not belong to a batch.");
			var batch = await Batches(connection)
				.SingleAsync(item => item.Id == batchHandle && item.State == BatchState.Executing, cancellationToken);
			var batchStamp = batch.ConcurrencyStamp;
			batch.TotalJobs += trackedAdditions;
			batch.PendingCount += trackedAdditions;
			batch.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken))
				throw new LostRaceException();
		}

		await ResetReturningGroupCursorsAsync(
			connection,
			[.. additions.Select(static addition => addition.Job)],
			cancellationToken
		);

		foreach (var addition in additions)
		{
			var job = ToEntity(addition.Job with
			{
				State = JobState.AwaitingContinuation,
				RemainingDependencies = 1,
			});
			_ = await InsertAsync(connection, job, cancellationToken);
			_ = await InsertAsync(connection, new ImmediateJobContinuationEntity
			{
				ChildJobHandle = job.Id,
				ParentKind = ContinuationParentKind.Job,
				ParentId = current.Id,
				Delay = addition.Delay.Ticks,
				Trigger = addition.Trigger,
			}, cancellationToken);

			if (addition.Options != ContinuationOptions.BeforeContinuations)
				continue;
			foreach (var waiter in waiters)
			{
				_ = await InsertAsync(connection, new ImmediateJobContinuationEntity
				{
					ChildJobHandle = waiter.Id,
					ParentKind = ContinuationParentKind.Job,
					ParentId = job.Id,
					Delay = 0,
					Trigger = ContinuationTrigger.Success,
				}, cancellationToken);
				var waiterStamp = waiter.ConcurrencyStamp;
				waiter.RemainingDependencies++;
				waiter.ConcurrencyStamp = Guid.NewGuid();
				if (!await UpdateJobAsync(connection, waiter, waiterStamp, cancellationToken))
					throw new LostRaceException();
			}
		}
	}

	private async Task<List<ImmediateJobEntity>> GetActiveWaitersAsync(
		DataConnection connection,
		JobHandle currentJobHandle,
		CancellationToken cancellationToken
	)
	{
		var waiterIds = await Continuations(connection)
			.Where(edge => edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == currentJobHandle.Value)
			.Select(edge => edge.ChildJobHandle)
			.Distinct()
			.ToListAsync(cancellationToken);
		return waiterIds.Count == 0
			? []
			: await Jobs(connection)
				.Where(job => waiterIds.Contains(job.Id) && job.State == JobState.AwaitingContinuation)
				.ToListAsync(cancellationToken);
	}

	private async Task PropagateTerminalAsync(
		DataConnection connection,
		ImmediateJobEntity terminalJob,
		DateTimeOffset now,
		ISet<(string QueueName, string GroupId)> terminalGroups,
		CancellationToken cancellationToken
	)
	{
		AddFairQueueGroup(terminalGroups, terminalJob);
		var parents = new Queue<(ContinuationParentKind Kind, string Id, ContinuationParentOutcome Outcome)>();
		var processed = new HashSet<(ContinuationParentKind Kind, string Id)>();
		parents.Enqueue((
			ContinuationParentKind.Job,
			terminalJob.Id,
			GetParentOutcome(terminalJob.State)
		));
		await UpdateBatchForTerminalJobAsync(connection, terminalJob, now, parents, cancellationToken);

		while (parents.TryDequeue(out var parent))
		{
			if (!processed.Add((parent.Kind, parent.Id)))
				continue;
			var edges = await Continuations(connection)
				.Where(edge => edge.ParentKind == parent.Kind
					&& edge.ParentId == parent.Id
					&& edge.ParentOutcome == ContinuationParentOutcome.Unsettled)
				.ToListAsync(cancellationToken);
			foreach (var edge in edges)
			{
				var settled = await Continuations(connection)
					.Where(entity => entity.ChildJobHandle == edge.ChildJobHandle
						&& entity.ParentKind == edge.ParentKind
						&& entity.ParentId == edge.ParentId
						&& entity.ParentOutcome == ContinuationParentOutcome.Unsettled)
					.Set(entity => entity.ParentOutcome, parent.Outcome)
					.UpdateAsync(cancellationToken);
				if (settled == 0)
					continue;
				var child = await Jobs(connection).SingleOrDefaultAsync(job => job.Id == edge.ChildJobHandle, cancellationToken);
				if (child is null || IsTerminal(child.State))
					continue;
				var childStamp = child.ConcurrencyStamp;
				if (child.State != JobState.AwaitingContinuation || child.RemainingDependencies <= 0)
					continue;
				child.RemainingDependencies--;
				if (parent.Outcome == ContinuationParentOutcome.Failed)
					child.FailedDependencies++;
				if (child.RemainingDependencies == 0)
				{
					if (await ShouldSkipSettledContinuationAsync(connection, child.Id, cancellationToken))
					{
						child.State = JobState.Skipped;
						child.CompletedAt = now;
						child.WorkerId = null;
						child.LeaseExpiresAt = null;
						AddFairQueueGroup(terminalGroups, child);
						parents.Enqueue((ContinuationParentKind.Job, child.Id, ContinuationParentOutcome.Other));
						await UpdateBatchForTerminalJobAsync(connection, child, now, parents, cancellationToken);
					}
					else
					{
						var delay = await GetMaximumContinuationDelayAsync(
							connection,
							child.Id,
							cancellationToken
						);
						var delayedDueAt = now + TimeSpan.FromTicks(delay.Ticks);
						if (child.DueAt < delayedDueAt)
							child.DueAt = delayedDueAt;
						child.State = child.DueAt <= now ? JobState.Pending : JobState.Scheduled;
					}
				}

				child.ConcurrencyStamp = Guid.NewGuid();
				if (!await UpdateJobAsync(connection, child, childStamp, cancellationToken))
					throw new LostRaceException();
			}
		}
	}

	private async Task<TimeSpan> GetMaximumContinuationDelayAsync(
		DataConnection connection,
		string childJobHandle,
		CancellationToken cancellationToken
	)
	{
		var delays = await Continuations(connection)
			.Where(edge => edge.ChildJobHandle == childJobHandle)
			.Select(edge => edge.Delay)
			.ToListAsync(cancellationToken);
		return delays.Count == 0
			? TimeSpan.Zero
			: TimeSpan.FromTicks(delays.Max());
	}

	private async Task<bool> ShouldSkipSettledContinuationAsync(
		DataConnection connection,
		string childJobHandle,
		CancellationToken cancellationToken
	)
	{
		var edges = await Continuations(connection)
			.Where(edge => edge.ChildJobHandle == childJobHandle)
			.ToListAsync(cancellationToken);
		var requiresFailure = false;
		var anyParentFailed = false;
		foreach (var edge in edges)
		{
			if (edge.Trigger == ContinuationTrigger.Success
				&& edge.ParentOutcome != ContinuationParentOutcome.Succeeded)
			{
				return true;
			}

			requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
			anyParentFailed |= edge.ParentOutcome == ContinuationParentOutcome.Failed;
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
		DateTimeOffset now,
		Queue<(ContinuationParentKind Kind, string Id, ContinuationParentOutcome Outcome)> parents,
		CancellationToken cancellationToken
	)
	{
		if (job.BatchHandle is not { } batchHandle)
			return;
		var batch = await Batches(connection).SingleAsync(item => item.Id == batchHandle, cancellationToken);
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
		if (batch.PendingCount == 0)
		{
			batch.State = GetTerminalBatchState(batch.FailedCount, batch.CancelledCount);
			batch.CompletedAt = now;
			parents.Enqueue((
				ContinuationParentKind.Batch,
				batch.Id,
				GetParentOutcome(batch.State)
			));
		}

		if (!await UpdateBatchAsync(connection, batch, oldStamp, cancellationToken))
			throw new LostRaceException();
	}

	private async Task EvaluateInitialDependenciesAsync(
		DataConnection connection,
		Dictionary<string, ImmediateJobEntity> jobs,
		List<ImmediateJobContinuationEntity> edges,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var externalJobHandles = edges
			.Where(edge => edge.ParentKind == ContinuationParentKind.Job && !jobs.ContainsKey(edge.ParentId))
			.Select(static edge => edge.ParentId)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToList();
		var externalBatchHandles = edges
			.Where(static edge => edge.ParentKind == ContinuationParentKind.Batch)
			.Select(static edge => edge.ParentId)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToList();
		var externalJobEntities = externalJobHandles.Count == 0
			? [with(StringComparer.Ordinal)]
			: (await Jobs(connection).Where(job => externalJobHandles.Contains(job.Id)).ToListAsync(cancellationToken)).ToDictionary(job => job.Id, StringComparer.Ordinal);
		var externalBatchEntities = externalBatchHandles.Count == 0
			? [with(StringComparer.Ordinal)]
			: (await Batches(connection).Where(batch => externalBatchHandles.Contains(batch.Id)).ToListAsync(cancellationToken)).ToDictionary(batch => batch.Id, StringComparer.Ordinal);
		if (externalJobEntities.Count != externalJobHandles.Count || externalBatchEntities.Count != externalBatchHandles.Count)
			throw new ImmediateJobException("A continuation parent does not exist.");
		foreach (var parentId in externalJobHandles)
		{
			var parent = externalJobEntities[parentId];
			if (IsTerminal(parent.State))
				continue;
			var oldStamp = parent.ConcurrencyStamp;
			parent.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, parent, oldStamp, cancellationToken))
				throw new LostRaceException();
		}

		foreach (var parentId in externalBatchHandles)
		{
			var parent = externalBatchEntities[parentId];
			if (parent.State != BatchState.Executing)
				continue;
			var oldStamp = parent.ConcurrencyStamp;
			parent.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateBatchAsync(connection, parent, oldStamp, cancellationToken))
				throw new LostRaceException();
		}

		var incoming = edges.ToLookup(static edge => edge.ChildJobHandle, StringComparer.Ordinal);
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
						externalJobEntities,
						externalBatchEntities
					);
					requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
					if (!terminal)
					{
						remaining++;
						continue;
					}

					edge.ParentOutcome = GetParentOutcome(parentSucceeded, parentFailed);
					var delayedDueAt = now + TimeSpan.FromTicks(edge.Delay);
					if (job.DueAt < delayedDueAt)
						job.DueAt = delayedDueAt;

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
		HashSet<string> jobHandles,
		IReadOnlyList<ImmediateJobContinuationEntity> edges
	)
	{
		var indegree = jobHandles.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
		var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var edge in edges.Where(edge =>
			edge.ParentKind == ContinuationParentKind.Job && jobHandles.Contains(edge.ParentId)))
		{
			indegree[edge.ChildJobHandle]++;
			if (!children.TryGetValue(edge.ParentId, out var values))
				children[edge.ParentId] = values = [];
			values.Add(edge.ChildJobHandle);
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

		if (visited != jobHandles.Count)
			throw new ImmediateJobException("The continuation graph contains a dependency cycle.");
	}

	private async ValueTask RetryConcurrencyAsync(
		Func<T, Task> operation,
		CancellationToken cancellationToken,
		int maxAttempts = MaxConcurrencyAttempts
	)
	{
		var concurrencyAttempt = 0;
		while (true)
		{
			await using var scope = contextScope.GetScope(out var connection);

			_ = await connection.BeginTransactionAsync(cancellationToken);
			try
			{
				await operation(connection);
				await connection.CommitTransactionAsync(cancellationToken);
				return;
			}
			catch (SyntheticExecutionInsertFailedException exception) when (++concurrencyAttempt < maxAttempts)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				if (!await SyntheticExecutionExistsAsync(exception.JobHandle, exception.Attempt, cancellationToken))
				{
					throw exception.DatabaseException;
				}
				// Retry in a new transaction and re-read the execution inserted by the winner.
				await DelayConcurrencyRetryAsync(cancellationToken);
			}
			catch (LostRaceException) when (++concurrencyAttempt < maxAttempts)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				await DelayConcurrencyRetryAsync(cancellationToken);
			}
			catch (SyntheticExecutionInsertFailedException exception)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				if (!await SyntheticExecutionExistsAsync(exception.JobHandle, exception.Attempt, cancellationToken))
				{
					throw exception.DatabaseException;
				}

				throw new ImmediateJobException(
					"The job operation could not be completed after repeated concurrency conflicts.",
					exception.DatabaseException
				);
			}
			catch (LostRaceException exception)
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				throw new ImmediateJobException(
					"The job operation could not be completed after repeated concurrency conflicts.",
					exception
				);
			}
			catch
			{
				await connection.RollbackTransactionAsync(cancellationToken);
				throw;
			}
		}
	}

	private async ValueTask<bool> SyntheticExecutionExistsAsync(
		JobHandle jobHandle,
		int attempt,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await using var scope = contextScope.GetScope(out var connection);

			return await Executions(connection)
				.AnyAsync(
					execution => execution.JobHandle == jobHandle.Value && execution.Attempt == attempt,
					cancellationToken
				);
		}
		catch (DbException)
		{
			return false;
		}
	}

	private static Task DelayConcurrencyRetryAsync(CancellationToken cancellationToken) =>
		Task.Delay(Random.Shared.Next(1, 6), cancellationToken);

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
			.Set(entity => entity.BatchHandle, job.BatchHandle)
			.Set(entity => entity.RemainingDependencies, job.RemainingDependencies)
			.Set(entity => entity.FailedDependencies, job.FailedDependencies)
			.Set(entity => entity.ConcurrencyStamp, job.ConcurrencyStamp)
			.UpdateAsync(cancellationToken);
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
			.UpdateAsync(cancellationToken);
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
			.Set(entity => entity.SkippedCount, batch.SkippedCount)
			.Set(entity => entity.StartedAt, batch.StartedAt)
			.Set(entity => entity.CompletedAt, batch.CompletedAt)
			.Set(entity => entity.State, batch.State)
			.Set(entity => entity.ConcurrencyStamp, batch.ConcurrencyStamp)
			.UpdateAsync(cancellationToken);
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
			.Set(entity => entity.QueueName, schedule.QueueName)
			.Set(entity => entity.Cron, schedule.Cron)
			.Set(entity => entity.TimeZone, schedule.TimeZone)
			.Set(entity => entity.IsCodeDefined, schedule.IsCodeDefined)
			.Set(entity => entity.IsPaused, schedule.IsPaused)
			.Set(entity => entity.NextRunAt, schedule.NextRunAt)
			.Set(entity => entity.LastRunAt, schedule.LastRunAt)
			.Set(entity => entity.ConcurrencyStamp, schedule.ConcurrencyStamp)
			.UpdateAsync(cancellationToken);
		return updated != 0;
	}

	private ITable<ImmediateJobEntity> Jobs(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobEntity>());

	private ITable<ImmediateJobExecutionEntity> Executions(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobExecutionEntity>());

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

	private ITable<TTable> WithSchema<TTable>(ITable<TTable> table)
		where TTable : notnull => _schema is null ? table : table.SchemaName(_schema);

	private Task<int> InsertAsync<TTable>(DataConnection connection, TTable entity, CancellationToken cancellationToken)
		where TTable : notnull => connection.InsertAsync(entity, schemaName: _schema, token: cancellationToken);

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

	private static BatchState GetTerminalBatchState(int failed, int cancelled)
	{
		if (failed != 0)
			return BatchState.Failed;
		return cancelled != 0 ? BatchState.Cancelled : BatchState.Succeeded;
	}

	private static ImmediateJobEntity ToEntity(JobRecord job) =>
		new()
		{
			Id = job.JobHandle.Value,
			QueueName = job.QueueName,
			JobName = job.JobName,
			Payload = job.Payload,
			Context = job.Context,
			GroupId = job.GroupId,
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
			BatchHandle = job.BatchHandle?.Value,
			RemainingDependencies = job.RemainingDependencies,
			FailedDependencies = job.FailedDependencies,
			ConcurrencyStamp = Guid.NewGuid(),
		};

	private async Task PrepareAcquisitionExecutionsAsync(
		DataConnection connection,
		JobRecord previous,
		string workerId,
		DateTimeOffset acquiredAt,
		CancellationToken cancellationToken
	)
	{
		var priorExecution = await GetOrMaterializeExecutionAsync(connection, ToEntity(previous), cancellationToken);
		if (previous.State == JobState.Active && priorExecution is not null)
		{
			_ = await Executions(connection)
				.Where(execution => execution.JobHandle == previous.JobHandle.Value && execution.Attempt == previous.Attempt)
				.Set(execution => execution.State, JobExecutionState.Interrupted)
				.Set(execution => execution.CompletedAt, previous.LeaseExpiresAt)
				.Set(execution => execution.Error, (string?)null)
				.UpdateAsync(cancellationToken);
		}

		_ = await InsertAsync(connection, new ImmediateJobExecutionEntity
		{
			JobHandle = previous.JobHandle.Value,
			Attempt = previous.Attempt + 1,
			State = JobExecutionState.Active,
			WorkerId = workerId,
			AcquiredAt = acquiredAt,
		}, cancellationToken);
	}

	private async Task<ImmediateJobExecutionEntity?> GetOrMaterializeExecutionAsync(
		DataConnection connection,
		ImmediateJobEntity job,
		CancellationToken cancellationToken
	)
	{
		if (job.Attempt <= 0)
			return null;
		var execution = await Executions(connection)
			.SingleOrDefaultAsync(
				item => item.JobHandle == job.Id && item.Attempt == job.Attempt,
				cancellationToken
			);
		if (execution is not null)
			return execution;

		var synthetic = JobExecutionRecord.CreateSynthetic(ToRecord(job));
		if (synthetic is null)
			return null;
		execution = ToEntity(synthetic);
		try
		{
			_ = await InsertAsync(connection, execution, cancellationToken);
		}
		catch (DbException exception)
		{
			throw new SyntheticExecutionInsertFailedException(JobHandle.FromString(job.Id), job.Attempt, exception);
		}

		return execution;
	}

	private static ImmediateJobExecutionEntity ToEntity(JobExecutionRecord execution) =>
		new()
		{
			JobHandle = execution.JobHandle.Value,
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

	private static JobExecutionRecord ToRecord(ImmediateJobExecutionEntity execution) =>
		new()
		{
			JobHandle = JobHandle.FromString(execution.JobHandle),
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
		var (parentKind, parentId) = (edge.ParentJobHandle, edge.ParentBatchHandle) switch
		{
			({ Value: { } jobHandle }, null) => (ContinuationParentKind.Job, jobHandle),
			(null, { Value: { } batchHandle }) => (ContinuationParentKind.Batch, batchHandle),
			_ => throw new ImmediateJobException("A continuation edge must identify exactly one parent job or batch."),
		};

		return new()
		{
			ChildJobHandle = edge.ChildJobHandle.Value,
			ParentKind = parentKind,
			ParentId = parentId,
			Trigger = edge.Trigger,
			Delay = edge.Delay.Ticks,
		};
	}

	private static JobRecord ToRecord(ImmediateJobEntity job) =>
		new()
		{
			JobHandle = JobHandle.FromString(job.Id),
			QueueName = job.QueueName,
			JobName = job.JobName,
			Payload = job.Payload,
			Context = job.Context,
			GroupId = job.GroupId,
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
			BatchHandle = BatchHandle.FromString(job.BatchHandle),
			RemainingDependencies = job.RemainingDependencies,
			FailedDependencies = job.FailedDependencies,
		};

	private static ImmediateRecurringJobEntity ToEntity(RecurringJobSchedule schedule) =>
		new()
		{
			Name = schedule.Name,
			JobName = schedule.JobName,
			QueueName = schedule.QueueName,
			Cron = schedule.Cron,
			TimeZone = schedule.TimeZone,
			IsCodeDefined = schedule.IsCodeDefined,
			IsPaused = schedule.IsPaused,
			NextRunAt = schedule.NextRunAt,
			LastRunAt = schedule.LastRunAt,
			ConcurrencyStamp = Guid.NewGuid(),
		};

	private static RecurringJobSchedule ToRecord(ImmediateRecurringJobEntity schedule) =>
		new()
		{
			Name = schedule.Name,
			JobName = schedule.JobName,
			QueueName = schedule.QueueName,
			Cron = schedule.Cron,
			TimeZone = schedule.TimeZone,
			IsCodeDefined = schedule.IsCodeDefined,
			IsPaused = schedule.IsPaused,
			NextRunAt = schedule.NextRunAt,
			LastRunAt = schedule.LastRunAt,
		};

	private static BatchStatus ToStatus(ImmediateJobBatchEntity batch) =>
		new()
		{
			BatchHandle = BatchHandle.FromString(batch.Id),
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

	private static JobContinuationEdge ToContinuationEdge(ImmediateJobContinuationEntity edge) =>
		new()
		{
			ChildJobHandle = JobHandle.FromString(edge.ChildJobHandle),
			ParentJobHandle = edge.ParentKind == ContinuationParentKind.Job ? JobHandle.FromString(edge.ParentId) : null,
			ParentBatchHandle = edge.ParentKind == ContinuationParentKind.Batch ? BatchHandle.FromString(edge.ParentId) : null,
			Delay = TimeSpan.FromTicks(edge.Delay),
			Trigger = edge.Trigger,
		};

#pragma warning disable CA1032, CA1064
	private sealed class LostRaceException : Exception;

	private sealed class SyntheticExecutionInsertFailedException(
		JobHandle jobHandle,
		int attempt,
		DbException databaseException
	) : Exception("A synthetic execution insert failed.", databaseException)
	{
		public JobHandle JobHandle { get; } = jobHandle;
		public int Attempt { get; } = attempt;
		public DbException DatabaseException { get; } = databaseException;
	}
#pragma warning restore CA1032, CA1064

	[LoggerMessage(
		EventId = LibraryEventIds.DisposeAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.DisposeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DisposeAsync called"
	)]
	private partial void DisposeAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.InitializeAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.InitializeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "InitializeAsync called"
	)]
	private partial void InitializeAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.LoadPersistedJobStateCalled,
		EventName = "Immediate.Jobs.LinqToDB.LoadPersistedJobStateCalled",
		Level = LogLevel.Debug,
		Message = "LoadPersistedJobState called (Jobs={Jobs}, Edges={Edges})"
	)]
	private partial void LoadPersistedJobStateCalled(int jobs, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.EnqueueAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueAsync called (JobHandle={JobHandle})"
	)]
	private partial void EnqueueAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetIncomingEdgesAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetIncomingEdgesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetIncomingEdgesAsync called (Jobs={Jobs})"
	)]
	private partial void GetIncomingEdgesAsyncCalled(int jobs);

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueContinuationAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.EnqueueContinuationAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueContinuationAsync called (JobHandle={JobHandle}, Edges={Edges})"
	)]
	private partial void EnqueueContinuationAsyncCalled(JobHandle jobHandle, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueBatchAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.EnqueueBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueBatchAsync called (BatchHandle={BatchHandle}, Jobs={Jobs}, Edges={Edges})"
	)]
	private partial void EnqueueBatchAsyncCalled(BatchHandle batchHandle, int jobs, int edges);

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
		EventId = LibraryEventIds.SetExecutionTelemetryAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.SetExecutionTelemetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SetExecutionTelemetryAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void SetExecutionTelemetryAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.RenewLeaseAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.RenewLeaseAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RenewLeaseAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void RenewLeaseAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.CompleteAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.CompleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.CompleteWithContinuationsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.CompleteWithContinuationsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CompleteWithContinuationsAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void CompleteWithContinuationsAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.AddBatchJobAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.AddBatchJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AddBatchJobAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void AddBatchJobAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.FailAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.FailAsyncCalled",
		Level = LogLevel.Debug,
		Message = "FailAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void FailAsyncCalled(JobHandle jobHandle, int execution);

	[LoggerMessage(
		EventId = LibraryEventIds.MergeRecurringSchedulesListAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.MergeRecurringSchedulesListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MergeRecurringSchedulesListAsync called"
	)]
	private partial void MergeRecurringSchedulesListAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.UpsertRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.UpsertRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpsertRecurringAsync called (Schedule={Schedule})"
	)]
	private partial void UpsertRecurringAsyncCalled(string schedule);

	[LoggerMessage(
		EventId = LibraryEventIds.RemoveRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.RemoveRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RemoveRecurringAsync called (Name={Name})"
	)]
	private partial void RemoveRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.PauseRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.PauseRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseRecurringAsync called (Name={Name})"
	)]
	private partial void PauseRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.ResumeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.ResumeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeRecurringAsync called (Name={Name})"
	)]
	private partial void ResumeRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.GetDueRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetDueRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetDueRecurringAsync called (BatchSize={BatchSize})"
	)]
	private partial void GetDueRecurringAsyncCalled(int batchSize);

	[LoggerMessage(
		EventId = LibraryEventIds.MaterializeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.MaterializeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MaterializeRecurringAsync called (JobHandle={JobHandle}, Schedule={Schedule})"
	)]
	private partial void MaterializeRecurringAsyncCalled(JobHandle jobHandle, string schedule);

	[LoggerMessage(
		EventId = LibraryEventIds.GetMonitoringSnapshotAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetMonitoringSnapshotAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetMonitoringSnapshotAsync called"
	)]
	private partial void GetMonitoringSnapshotAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.QueryJobsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.QueryJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobsAsync called (Query={Query})"
	)]
	private partial void QueryJobsAsyncCalled(JobQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryJobExecutionsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.QueryJobExecutionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobExecutionsAsync called (JobHandle={JobHandle})"
	)]
	private partial void QueryJobExecutionsAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetBatchStatusAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetBatchStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetBatchStatusAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void GetBatchStatusAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryBatchesAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.QueryBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryBatchesAsync called (Query={Query})"
	)]
	private partial void QueryBatchesAsyncCalled(BatchQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryBatchMembersAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.QueryBatchMembersAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryBatchMembersAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void QueryBatchMembersAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetBatchGraphAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetBatchGraphAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetBatchGraphAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void GetBatchGraphAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobStatusAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetJobStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobStatusAsync called (JobHandle={JobHandle})"
	)]
	private partial void GetJobStatusAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.CancelBatchAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.CancelBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void CancelBatchAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.DeleteBatchAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.DeleteBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DeleteBatchAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void DeleteBatchAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.CancelAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.CancelAsyncCalled",
		Level = LogLevel.Debug,
		Message = "CancelAsync called (JobHandle={JobHandle})"
	)]
	private partial void CancelAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.RetryAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.RetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RetryAsync called (JobHandle={JobHandle})"
	)]
	private partial void RetryAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.DeleteAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.DeleteAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DeleteAsync called (JobHandle={JobHandle})"
	)]
	private partial void DeleteAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.PurgeJobsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.PurgeJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeJobsAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeJobsAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);

	[LoggerMessage(
		EventId = LibraryEventIds.PurgeBatchesAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.PurgeBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PurgeBatchesAsync called (SucceededRetention={SucceededRetention}, FailedRetention={FailedRetention})"
	)]
	private partial void PurgeBatchesAsyncCalled(TimeSpan succeededRetention, TimeSpan failedRetention);

	[LoggerMessage(
		EventId = LibraryEventIds.HeartbeatAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.HeartbeatAsyncCalled",
		Level = LogLevel.Debug,
		Message = "HeartbeatAsync called (Server={Server})"
	)]
	private partial void HeartbeatAsyncCalled(string server);

	[LoggerMessage(
		EventId = LibraryEventIds.IsHealthyAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.IsHealthyAsyncCalled",
		Level = LogLevel.Debug,
		Message = "IsHealthyAsync called"
	)]
	private partial void IsHealthyAsyncCalled();
}

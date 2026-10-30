using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	private async Task FlushContinuationAdditionsAsync(
		DataConnection connection,
		ImmediateJobEntity current,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken
	)
	{
		var jobs = await CreateRecordsAsync(connection, [.. additions.Select(static addition => addition.Job)], cancellationToken);
		var records = jobs.ToDictionary(static job => job.JobHandle);

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

		foreach (var addition in additions)
		{
			var job = ToEntity(records[addition.Job.JobHandle] with
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
				Delay = addition.Delay.ToString(),
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
					Delay = TimeSpan.Zero.ToString(),
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
		return await Jobs(connection)
			.Where(job => (job.State == JobState.AwaitingContinuation || job.State == JobState.WaitingForTrigger)
				&& Continuations(connection).Any(edge => edge.ChildJobHandle == job.Id
					&& edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == currentJobHandle.Value))
			.ToListAsync(cancellationToken);
	}

	private Task PropagateTerminalAsync(
		DataConnection connection, ImmediateJobEntity terminalJob, DateTimeOffset now,
		ISet<string> terminalGroups, CancellationToken cancellationToken
	) => PropagateTerminalAsync(connection, [terminalJob], now, terminalGroups, cancellationToken);

	private async Task PropagateTerminalAsync(
		DataConnection connection, IReadOnlyList<ImmediateJobEntity> terminalJobs, DateTimeOffset now,
		ISet<string> terminalGroups, CancellationToken cancellationToken
	)
	{
		var parents = new Queue<(ContinuationParentKind Kind, string Id, ContinuationParentOutcome Outcome)>();
		var processed = new HashSet<(ContinuationParentKind Kind, string Id)>();
		// These snapshots belong only to this transaction. Every write still checks its original stamp.
		var batches = new Dictionary<string, ImmediateJobBatchEntity>(StringComparer.Ordinal);
		var batchStamps = new Dictionary<string, Guid>(StringComparer.Ordinal);
		await LoadBatchesAsync(terminalJobs);
		foreach (var job in terminalJobs)
		{
			AddFairQueueGroup(terminalGroups, job);
			parents.Enqueue((ContinuationParentKind.Job, job.Id, GetParentOutcome(job.State)));
			ApplyTerminalJobToBatch(job, batches, now, parents);
		}

		while (parents.Count != 0)
		{
			var frontier = new Dictionary<(ContinuationParentKind Kind, string Id), ContinuationParentOutcome>();
			while (frontier.Count < 256 && parents.TryDequeue(out var parent))
			{
				if (processed.Add((parent.Kind, parent.Id)))
					frontier.Add((parent.Kind, parent.Id), parent.Outcome);
			}

			if (frontier.Count == 0)
				continue;
			var jobIds = frontier.Keys.Where(static key => key.Kind == ContinuationParentKind.Job).Select(static key => key.Id).ToList();
			var batchIds = frontier.Keys.Where(static key => key.Kind == ContinuationParentKind.Batch).Select(static key => key.Id).ToList();
			var edges = await Continuations(connection)
				.Where(edge => edge.ParentOutcome == ContinuationParentOutcome.Unsettled &&
					((edge.ParentKind == ContinuationParentKind.Job && jobIds.Contains(edge.ParentId)) ||
					(edge.ParentKind == ContinuationParentKind.Batch && batchIds.Contains(edge.ParentId))))
				.Select(edge => edge.ChildJobHandle).Distinct().ToListAsync(cancellationToken);
			foreach (var childIds in edges.Chunk(256))
			{
				var children = await Jobs(connection).Where(job => childIds.Contains(job.Id))
					.ToDictionaryAsync(static job => job.Id, StringComparer.Ordinal, cancellationToken);
				var incoming = (await Continuations(connection).Where(edge => childIds.Contains(edge.ChildJobHandle))
					.ToListAsync(cancellationToken)).ToLookup(static edge => edge.ChildJobHandle, StringComparer.Ordinal);
				await LoadBatchesAsync(children.Values);
				foreach (var childId in childIds)
				{
					children.TryGetValue(childId, out var child);
					var oldStamp = child?.ConcurrencyStamp;
					var changed = false;
					foreach (var edge in incoming[childId])
					{
						if (edge.ParentOutcome != ContinuationParentOutcome.Unsettled || !frontier.TryGetValue((edge.ParentKind, edge.ParentId), out var outcome))
							continue;
						var settled = await Continuations(connection)
							.Where(item => item.ChildJobHandle == edge.ChildJobHandle && item.ParentKind == edge.ParentKind
								&& item.ParentId == edge.ParentId && item.ParentOutcome == ContinuationParentOutcome.Unsettled)
							.Set(item => item.ParentOutcome, outcome).UpdateAsync(cancellationToken);
						if (settled == 0)
							throw new LostRaceException();
						edge.ParentOutcome = outcome;
						if (child is null || child.State is not (JobState.AwaitingContinuation or JobState.WaitingForTrigger) || child.RemainingDependencies <= 0)
							continue;
						changed = true;
						child.RemainingDependencies--;
						if (outcome == ContinuationParentOutcome.Failed)
							child.FailedDependencies++;
						if (child.RemainingDependencies != 0)
							continue;
						var (skip, delay) = EvaluateContinuation(incoming[childId]);
						if (skip)
						{
							child.State = JobState.Skipped;
							child.CompletedAt = now;
							child.WorkerId = null;
							child.LeaseExpiresAt = null;
							AddFairQueueGroup(terminalGroups, child);
							parents.Enqueue((ContinuationParentKind.Job, child.Id, ContinuationParentOutcome.Other));
							ApplyTerminalJobToBatch(child, batches, now, parents);
						}
						else if (child.State != JobState.WaitingForTrigger)
						{
							var delayedDueAt = now + delay;
							if (child.DueAt < delayedDueAt)
								child.DueAt = delayedDueAt;
							child.State = child.DueAt <= now ? JobState.Pending : JobState.Scheduled;
						}
					}

					if (changed && child is not null)
					{
						child.ConcurrencyStamp = Guid.NewGuid();
						if (!await UpdateJobAsync(connection, child, oldStamp!.Value, cancellationToken))
							throw new LostRaceException();
					}
				}
			}
		}

		foreach (var batch in batches.Values.OrderBy(static batch => batch.Id, StringComparer.Ordinal))
			if (batch.ConcurrencyStamp != batchStamps[batch.Id] && !await UpdateBatchAsync(connection, batch, batchStamps[batch.Id], cancellationToken))
				throw new LostRaceException();

		async Task LoadBatchesAsync(IEnumerable<ImmediateJobEntity> jobs)
		{
			foreach (var ids in jobs.Where(static job => job.BatchHandle != null).Select(static job => job.BatchHandle!)
				.Distinct(StringComparer.Ordinal).Where(id => !batches.ContainsKey(id)).Chunk(256))
			{
				var loaded = await Batches(connection).Where(batch => ids.Contains(batch.Id)).ToListAsync(cancellationToken);
				if (loaded.Count != ids.Length)
					throw new LostRaceException();
				foreach (var batch in loaded)
				{
					batches.Add(batch.Id, batch);
					batchStamps.Add(batch.Id, batch.ConcurrencyStamp);
				}
			}
		}
	}

	private static (bool Skip, TimeSpan Delay) EvaluateContinuation(IEnumerable<ImmediateJobContinuationEntity> edges)
	{
		var requiresFailure = false;
		var anyFailed = false;
		var violated = false;
		TimeSpan? delay = null;
		foreach (var edge in edges)
		{
			violated |= edge.Trigger == ContinuationTrigger.Success && edge.ParentOutcome != ContinuationParentOutcome.Succeeded;
			requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
			anyFailed |= edge.ParentOutcome == ContinuationParentOutcome.Failed;
			var edgeDelay = TimeSpan.Parse(edge.Delay, CultureInfo.InvariantCulture);
			if (delay is null || edgeDelay > delay)
				delay = edgeDelay;
		}

		return (violated || (requiresFailure && !anyFailed), delay ?? TimeSpan.Zero);
	}

	private static void ApplyTerminalJobToBatch(
		ImmediateJobEntity job, Dictionary<string, ImmediateJobBatchEntity> batches, DateTimeOffset now,
		Queue<(ContinuationParentKind Kind, string Id, ContinuationParentOutcome Outcome)> parents
	)
	{
		if (job.BatchHandle is not { } batchHandle)
			return;
		var batch = batches[batchHandle];
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
			case JobState.WaitingForTrigger:
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
			if (IsTerminal(parent.State))
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
					var delayedDueAt = now + TimeSpan.Parse(edge.Delay, CultureInfo.InvariantCulture);
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
					if (job.State != JobState.WaitingForTrigger)
						job.State = job.DueAt <= now ? JobState.Pending : JobState.Scheduled;
					job.RemainingDependencies = 0;
				}
				else
				{
					if (job.State != JobState.WaitingForTrigger)
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
			return (IsTerminal(state), state == BatchState.Succeeded, state == BatchState.Failed);
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

	private static bool IsTerminal(JobState state) =>
		state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;

	private static bool IsTerminal(BatchState state) =>
		state is not (BatchState.Executing or BatchState.WaitingForTrigger);

	private static ContinuationParentOutcome GetParentOutcome(JobState state) => state switch
	{
		JobState.Succeeded => ContinuationParentOutcome.Succeeded,
		JobState.Failed => ContinuationParentOutcome.Failed,
		JobState.AwaitingContinuation or
		JobState.WaitingForTrigger or
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
		BatchState.Executing or BatchState.WaitingForTrigger or BatchState.Cancelled => ContinuationParentOutcome.Other,
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
}

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
				.Where(job => waiterIds.Contains(job.Id) && (job.State == JobState.AwaitingContinuation || job.State == JobState.WaitingForTrigger))
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
				if (child.State is not (JobState.AwaitingContinuation or JobState.WaitingForTrigger) || child.RemainingDependencies <= 0)
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
					else if (child.State != JobState.WaitingForTrigger)
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
			case JobState.WaitingForTrigger:
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

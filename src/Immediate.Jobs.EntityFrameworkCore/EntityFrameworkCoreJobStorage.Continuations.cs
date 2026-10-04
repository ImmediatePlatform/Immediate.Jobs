using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	private static async Task FlushContinuationAdditionsAsync(
		TContext context,
		ImmediateJobEntity current,
		IReadOnlyList<JobContinuationAddition> additions,
		CancellationToken cancellationToken
	)
	{
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var trackedAdditions = 0;
		foreach (var addition in additions)
		{
			if (!ids.Add(addition.Job.JobHandle.Value))
				throw new ImmediateJobException("Buffered continuations contain duplicate job identifiers.");
			if (!Enum.IsDefined(addition.Trigger))
				throw new ArgumentOutOfRangeException(nameof(additions), "Unknown continuation trigger.");
			if (addition.Job.State is not (JobState.Pending or JobState.Scheduled))
				throw new ImmediateJobException($"Dynamic continuation '{addition.Job.JobHandle}' has invalid state '{addition.Job.State}'.");

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
			? await GetActiveWaitersAsync(context, JobHandle.FromString(current.Id), cancellationToken)
			: [];
		ImmediateJobBatchEntity? batch = null;
		if (trackedAdditions != 0)
		{
			if (current.BatchHandle is not { } batchHandle)
				throw new ImmediateJobException("The current job does not belong to a batch.");
			batch = await context.Set<ImmediateJobBatchEntity>()
				.SingleAsync(item => item.Id == batchHandle && item.State == BatchState.Executing, cancellationToken);
			batch.TotalJobs += trackedAdditions;
			batch.PendingCount += trackedAdditions;
			batch.ConcurrencyStamp = Guid.NewGuid();
		}

		foreach (var addition in additions)
		{
			var job = ToEntity(addition.Job with
			{
				State = JobState.AwaitingContinuation,
				RemainingDependencies = 1,
			});
			_ = context.Add(job);
			_ = context.Add(new ImmediateJobContinuationEntity
			{
				ChildJobHandle = job.Id,
				ParentKind = ContinuationParentKind.Job,
				ParentId = current.Id,
				Delay = addition.Delay.Ticks,
				Trigger = addition.Trigger,
			});

			if (addition.Options != ContinuationOptions.BeforeContinuations)
				continue;
			foreach (var waiter in waiters)
			{
				_ = context.Add(new ImmediateJobContinuationEntity
				{
					ChildJobHandle = waiter.Id,
					ParentKind = ContinuationParentKind.Job,
					ParentId = job.Id,
					Delay = 0,
					Trigger = ContinuationTrigger.Success,
				});
				waiter.RemainingDependencies++;
				waiter.ConcurrencyStamp = Guid.NewGuid();
			}
		}
	}

	private static async Task<List<ImmediateJobEntity>> GetActiveWaitersAsync(
		TContext context,
		JobHandle currentJobHandle,
		CancellationToken cancellationToken
	)
	{
		var waiterIds = await context.Set<ImmediateJobContinuationEntity>()
			.Where(edge => edge.ParentKind == ContinuationParentKind.Job && edge.ParentId == currentJobHandle.Value)
			.Select(edge => edge.ChildJobHandle)
			.Distinct()
			.ToListAsync(cancellationToken);
		return waiterIds.Count == 0
			? []
			: await context.Set<ImmediateJobEntity>()
				.Where(job => waiterIds.Contains(job.Id) && (job.State == JobState.AwaitingContinuation || job.State == JobState.WaitingForTrigger))
				.ToListAsync(cancellationToken);
	}

	private static async Task PropagateTerminalAsync(
		TContext context,
		ImmediateJobEntity terminalJob,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var parents = new Queue<(ContinuationParentKind Kind, string Id, ContinuationParentOutcome Outcome)>();
		var processed = new HashSet<(ContinuationParentKind Kind, string Id)>();
		parents.Enqueue((
			ContinuationParentKind.Job,
			terminalJob.Id,
			GetParentOutcome(terminalJob.State)
		));
		await UpdateBatchForTerminalJobAsync(context, terminalJob, now, parents, cancellationToken);

		while (parents.TryDequeue(out var parent))
		{
			if (!processed.Add((parent.Kind, parent.Id)))
				continue;
			var edges = await context.Set<ImmediateJobContinuationEntity>()
				.Where(edge => edge.ParentKind == parent.Kind
					&& edge.ParentId == parent.Id
					&& edge.ParentOutcome == ContinuationParentOutcome.Unsettled)
				.ToListAsync(cancellationToken);
			foreach (var edge in edges)
			{
				edge.ParentOutcome = parent.Outcome;
				var child = await context.Set<ImmediateJobEntity>()
					.SingleOrDefaultAsync(job => job.Id == edge.ChildJobHandle, cancellationToken);
				if (child is null || IsTerminal(child.State))
					continue;

				if (child.State is not (JobState.AwaitingContinuation or JobState.WaitingForTrigger) || child.RemainingDependencies <= 0)
					continue;
				child.RemainingDependencies--;
				if (parent.Outcome == ContinuationParentOutcome.Failed)
					child.FailedDependencies++;
				if (child.RemainingDependencies == 0)
				{
					var skip = await ShouldSkipSettledContinuationAsync(
						context,
						child.Id,
						cancellationToken
					);
					if (skip)
					{
						child.State = JobState.Skipped;
						child.CompletedAt = now;
						parents.Enqueue((ContinuationParentKind.Job, child.Id, ContinuationParentOutcome.Other));
						await UpdateBatchForTerminalJobAsync(context, child, now, parents, cancellationToken);
					}
					else if (child.State != JobState.WaitingForTrigger)
					{
						var delay = await GetMaximumContinuationDelayAsync(
							context,
							child.Id,
							cancellationToken
						);
						var delayedDueAt = now + delay;
						if (child.DueAt < delayedDueAt)
							child.DueAt = delayedDueAt;
						child.State = child.DueAt <= now ? JobState.Pending : JobState.Scheduled;
					}
				}

				child.ConcurrencyStamp = Guid.NewGuid();
			}
		}
	}

	private static async Task<TimeSpan> GetMaximumContinuationDelayAsync(
		TContext context,
		string childJobHandle,
		CancellationToken cancellationToken
	)
	{
		var delays = await context.Set<ImmediateJobContinuationEntity>()
			.Where(edge => edge.ChildJobHandle == childJobHandle)
			.Select(edge => edge.Delay)
			.ToListAsync(cancellationToken);
		return delays.Count == 0
			? TimeSpan.Zero
			: TimeSpan.FromTicks(delays.Max());
	}

	private static async Task<bool> ShouldSkipSettledContinuationAsync(
		TContext context,
		string childJobHandle,
		CancellationToken cancellationToken
	)
	{
		var edges = await context.Set<ImmediateJobContinuationEntity>()
			.Where(edge => edge.ChildJobHandle == childJobHandle)
			.ToListAsync(cancellationToken);
		var requiresFailure = false;
		var anyFailed = false;
		foreach (var edge in edges)
		{
			if (edge.Trigger == ContinuationTrigger.Success
				&& edge.ParentOutcome != ContinuationParentOutcome.Succeeded)
			{
				return true;
			}

			requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
			anyFailed |= edge.ParentOutcome == ContinuationParentOutcome.Failed;
		}

		return requiresFailure && !anyFailed;
	}

	private static async Task UpdateBatchForTerminalJobAsync(
		TContext context,
		ImmediateJobEntity job,
		DateTimeOffset now,
		Queue<(ContinuationParentKind Kind, string Id, ContinuationParentOutcome Outcome)> parents,
		CancellationToken cancellationToken
	)
	{
		if (job.BatchHandle is not { } batchHandle)
			return;
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleAsync(item => item.Id == batchHandle, cancellationToken);
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

	private static async Task EvaluateInitialDependenciesAsync(
		TContext context,
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
			? []
			: await context.Set<ImmediateJobEntity>()
				.Where(job => externalJobHandles.Contains(job.Id))
				.OrderBy(static job => job.Id)
				.ToListAsync(cancellationToken);
		var externalBatchEntities = externalBatchHandles.Count == 0
			? []
			: await context.Set<ImmediateJobBatchEntity>()
				.Where(batch => externalBatchHandles.Contains(batch.Id))
				.OrderBy(static batch => batch.Id)
				.ToListAsync(cancellationToken);
		var externalJobs = externalJobEntities.ToDictionary(job => job.Id, StringComparer.Ordinal);
		var externalBatches = externalBatchEntities.ToDictionary(batch => batch.Id, StringComparer.Ordinal);
		if (externalJobs.Count != externalJobHandles.Count || externalBatches.Count != externalBatchHandles.Count)
			throw new ImmediateJobException("A continuation parent does not exist.");
		foreach (var parent in externalJobEntities.Where(parent => !IsTerminal(parent.State)))
			parent.ConcurrencyStamp = Guid.NewGuid();
		foreach (var parent in externalBatchEntities.Where(parent => !IsTerminal(parent.State)))
			parent.ConcurrencyStamp = Guid.NewGuid();

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
						externalJobs,
						externalBatches
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

	private static BatchState GetTerminalBatchState(int failed, int cancelled) =>
		failed != 0 ? BatchState.Failed : cancelled != 0 ? BatchState.Cancelled : BatchState.Succeeded;
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock; this method does not access mutable storage state.
	/// </remarks>
	private static JobRecord NormalizeWaitingJob(JobRecord job, int dependencyCount) => job with
	{
		State = dependencyCount == 0 || job.State == JobState.WaitingForTrigger ? job.State : JobState.AwaitingContinuation,
		RemainingDependencies = dependencyCount,
		FailedDependencies = 0,
		WorkerId = null,
		LeaseExpiresAt = null,
		CompletedAt = null,
	};

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void EvaluateAlreadyTerminalParents(IReadOnlyList<JobContinuationEdge> edges)
	{
		foreach (var parentId in edges
			.Where(static edge => edge.ParentJobHandle is not null)
			.Select(static edge => edge.ParentJobHandle!)
			.Distinct()
			.Where(parentId => _jobs.TryGetValue(parentId, out var parent) && IsTerminal(parent.State))
			.ToList())
		{
			ProcessTerminalJob(parentId);
		}

		foreach (var parentId in edges
			.Where(static edge => edge.ParentBatchHandle is not null)
			.Select(static edge => edge.ParentBatchHandle!)
			.Distinct()
			.Where(parentId => _batches.TryGetValue(parentId, out var parent) && IsTerminal(parent.State))
			.ToList())
		{
			ProcessTerminalBatch(parentId);
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void ProcessTerminalJob(JobHandle parentJobHandle)
	{
		if (!_jobs.TryGetValue(parentJobHandle, out var parent) || !IsTerminal(parent.State))
			return;

		foreach (var edge in _edges
			.Where(edge => edge.ParentJobHandle == parentJobHandle && !_settledEdges.Contains(edge)))
		{
			_settledEdges.Add(edge);
			SettleEdge(edge, parentFailed: parent.State == JobState.Failed);
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void ProcessTerminalBatch(BatchHandle parentBatchHandle)
	{
		if (!_batches.TryGetValue(parentBatchHandle, out var parent) || !IsTerminal(parent.State))
			return;

		foreach (var edge in _edges
			.Where(edge => edge.ParentBatchHandle == parentBatchHandle && !_settledEdges.Contains(edge)))
		{
			_settledEdges.Add(edge);
			SettleEdge(edge, parentFailed: parent.State == BatchState.Failed);
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void SettleEdge(JobContinuationEdge edge, bool parentFailed)
	{
		ref var child = ref CollectionsMarshal.GetValueRefOrNullRef(_jobs, edge.ChildJobHandle);
		if (Unsafe.IsNullRef(ref child) || IsTerminal(child.State))
			return;

		var remaining = Math.Max(0, child.RemainingDependencies - 1);
		if (remaining != 0)
		{
			UpdateJob(ref child, child with
			{
				RemainingDependencies = remaining,
				FailedDependencies = child.FailedDependencies + (parentFailed ? 1 : 0),
			});
			return;
		}

		var (triggersSatisfied, failedDependencies, delay) = EvaluateIncomingTriggers(child.JobHandle);

		var evaluateDelay = triggersSatisfied && child.State == JobState.AwaitingContinuation
			? timeProvider.GetUtcNow() + delay
			: DateTimeOffset.UnixEpoch;

		var dueAt = child.DueAt > evaluateDelay ? child.DueAt : evaluateDelay;

		UpdateJob(ref child, child with
		{
			State = triggersSatisfied && child.State == JobState.AwaitingContinuation
				? dueAt <= timeProvider.GetUtcNow() ? JobState.Pending : JobState.Scheduled
				: child.State,
			DueAt = dueAt,
			RemainingDependencies = 0,
			FailedDependencies = failedDependencies,
		});

		if (!triggersSatisfied)
			TransitionToTerminal(ref child, JobState.Skipped, error: null, timeProvider.GetUtcNow());
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private (bool Satisfied, int FailedDependencies, TimeSpan Delay) EvaluateIncomingTriggers(JobHandle childJobHandle)
	{
		var allTerminal = true;
		var requiresFailure = false;
		var successViolated = false;
		var failedDependencies = 0;
		var delay = TimeSpan.Zero;

		foreach (var edge in _edges.Where(edge => edge.ChildJobHandle == childJobHandle))
		{
			var parentTerminal = false;
			var parentSucceeded = false;
			var parentFailed = false;

			if (edge.ParentJobHandle is { } parentJobHandle && _jobs.TryGetValue(parentJobHandle, out var parentJob))
			{
				parentTerminal = IsTerminal(parentJob.State);
				parentSucceeded = parentJob.State == JobState.Succeeded;
				parentFailed = parentJob.State == JobState.Failed;
			}
			else if (edge.ParentBatchHandle is { } parentBatchHandle && _batches.TryGetValue(parentBatchHandle, out var parentBatch))
			{
				parentTerminal = IsTerminal(parentBatch.State);
				parentSucceeded = parentBatch.State == BatchState.Succeeded;
				parentFailed = parentBatch.State == BatchState.Failed;
			}

			allTerminal &= parentTerminal;
			failedDependencies += parentFailed ? 1 : 0;
			requiresFailure |= edge.Trigger == ContinuationTrigger.Failure;
			successViolated |= edge.Trigger == ContinuationTrigger.Success && !parentSucceeded;
			delay = edge.Delay > delay ? edge.Delay : delay;
		}

		return (
			allTerminal && !successViolated && (!requiresFailure || failedDependencies != 0),
			failedDependencies,
			delay
		);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private JobContinuationEdge[] GetUnsettledWaiters(JobHandle parentJobHandle) =>
	[
		.. _edges.Where(edge => edge.ParentJobHandle == parentJobHandle &&
			!_settledEdges.Contains(edge) &&
			_jobs.TryGetValue(edge.ChildJobHandle, out var child) &&
			!IsTerminal(child.State)),
	];

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void SpliceBeforeWaiters(
		JobHandle newParentJobHandle,
		IReadOnlyList<JobContinuationEdge> existingWaiters
	)
	{
		foreach (var existingEdge in existingWaiters)
		{
			ref var child = ref CollectionsMarshal.GetValueRefOrNullRef(_jobs, existingEdge.ChildJobHandle);
			if (Unsafe.IsNullRef(ref child) || IsTerminal(child.State))
				continue;

			UpdateJob(ref child, child with
			{
				State = child.State == JobState.WaitingForTrigger ? JobState.WaitingForTrigger : JobState.AwaitingContinuation,
				RemainingDependencies = child.RemainingDependencies + 1,
			});
			_edges.Add(new()
			{
				ChildJobHandle = child.JobHandle,
				ParentJobHandle = newParentJobHandle,
				Trigger = ContinuationTrigger.Success,
				Delay = TimeSpan.Zero,
			});
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void ValidateSplice(IReadOnlyList<JobContinuationEdge> existingWaiters, int additions)
	{
		if (additions == 0)
			return;
		foreach (var existingEdge in existingWaiters)
		{
			if (_jobs.TryGetValue(existingEdge.ChildJobHandle, out var child) &&
				child.RemainingDependencies > int.MaxValue - additions)
			{
				throw new ImmediateJobException($"Continuation dependency count overflow for job '{child.JobHandle}'.");
			}
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void IncrementBatchMembers(BatchHandle? batchHandle, int count)
	{
		if (count == 0)
			return;

		if (batchHandle is null)
			throw new ImmediateJobException("The current job's batch was not found.");

		ref var batch = ref ValidateExistingElement(_batches, batchHandle);

		if (batch.TotalJobs > int.MaxValue - count || batch.PendingCount > int.MaxValue - count)
			throw new ImmediateJobException($"Batch '{batchHandle}' member count overflow.");

		batch = batch with
		{
			TotalJobs = batch.TotalJobs + count,
			PendingCount = batch.PendingCount + count,
		};
	}

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock; this method does not access mutable storage state.
	/// </remarks>
	private static bool IsTerminal(JobState state) =>
		state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock; this method does not access mutable storage state.
	/// </remarks>
	private static bool IsTerminal(BatchState state) =>
		state is not (BatchState.Executing or BatchState.WaitingForTrigger);
}

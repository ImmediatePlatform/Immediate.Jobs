using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class GraphStorageConformance
{
	private const string CapabilityName = "Graph.Capability.ResolvesAdvertisedStorage";
	private const string BatchLifecycleName = "Graph.Batches.CommitsProjectsAndDeletesAtomically";
	private const string InvalidBatchName = "Graph.Batches.RollbackInvalidBatchWithoutPartialWrites";
	private const string TriggerName = "Graph.Dependencies.ReleasesAndSkipsConditionalBranches";
	private const string FanInName = "Graph.Dependencies.FanInWaitsForEveryParent";
	private const string PropagationDelayName = "Graph.Dependencies.AppliesDelayFromParentSettlement";
	private const string DynamicName = "Graph.Dynamic.SpliceAndOwnershipAreAtomic";
	private const string StaleDynamicName = "Graph.Dynamic.RejectsStaleActiveExecution";
	private const string TerminalParentName = "Graph.Dependencies.EvaluatesAlreadyTerminalParents";
	private const string InvalidDynamicName = "Graph.Dynamic.RejectsInvalidBatchRelationshipsAtomically";
	private const string CancellationName = "Graph.Maintenance.CancelsUnsettledDependencyChains";
	private const string BatchPurgeName = "Graph.Maintenance.PurgesExpiredBatchesWithContinuations";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(CapabilityName, StorageCapabilities.Graph, ResolvesAdvertisedStorage),
		new(BatchLifecycleName, StorageCapabilities.Graph, BatchLifecycleAsync),
		new(InvalidBatchName, StorageCapabilities.Graph, InvalidBatchRollsBackAsync),
		new(TriggerName, StorageCapabilities.Graph, ConditionalTriggersAsync),
		new(FanInName, StorageCapabilities.Graph, FanInAsync),
		new(PropagationDelayName, StorageCapabilities.Graph, PropagationDelayAsync),
		new(DynamicName, StorageCapabilities.Graph, DynamicContinuationsAsync),
		new(StaleDynamicName, StorageCapabilities.Graph, RejectsStaleActiveExecutionAsync),
		new(TerminalParentName, StorageCapabilities.Graph, EvaluatesAlreadyTerminalParentsAsync),
		new(InvalidDynamicName, StorageCapabilities.Graph, RejectsInvalidBatchRelationshipsAsync),
		new(CancellationName, StorageCapabilities.Graph, CancelUnsettledChainAsync),
		new(BatchPurgeName, StorageCapabilities.Graph, PurgeExpiredBatchAsync),
	];

	private static async ValueTask PropagationDelayAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, PropagationDelayName);
		var parent = CreateJob("delayed-parent", batchHandle: "delayed-batch");
		var child = CreateJob("delayed-child", batchHandle: "delayed-batch") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await graph.EnqueueBatchAsync(
			CreateBatch("delayed-batch", 2),
			[parent, child],
			[new() { ChildJobHandle = child.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.FromMinutes(10) }],
			cancellationToken
		).ConfigureAwait(false);

		var execution = ConformanceAssert.NotNull(
			(await graph.AcquireDueJobsAsync(CreateRequest("delayed-worker", parent.JobName), cancellationToken)
				.ConfigureAwait(false)).SingleOrDefault(),
			PropagationDelayName,
			"the parent must be acquirable"
		);
		timeProvider.Advance(TimeSpan.FromMinutes(20));
		var settledAt = timeProvider.GetUtcNow();
		await graph.CompleteAsync(parent.JobHandle, execution.Attempt, "delayed-worker", cancellationToken).ConfigureAwait(false);

		var released = ConformanceAssert.NotNull(
			(await graph.QueryJobsAsync(new() { JobHandle = child.JobHandle }, cancellationToken).ConfigureAwait(false)).SingleOrDefault(),
			PropagationDelayName,
			"the delayed child must remain queryable"
		);
		ConformanceAssert.Equal(JobState.Scheduled, released.State, PropagationDelayName,
			"the child must remain scheduled during its post-parent delay");
		ConformanceAssert.Equal(settledAt.AddMinutes(10), released.DueAt, PropagationDelayName,
			"the child due time must be based on parent settlement rather than enqueue time");
	}

	private static ValueTask ResolvesAdvertisedStorage(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_ = GetGraph(storage, CapabilityName);
		return ValueTask.CompletedTask;
	}

	private static async ValueTask BatchLifecycleAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, BatchLifecycleName);
		var batchHandle = BatchHandle.FromString("lifecycle-batch");
		var parent = CreateJob(JobHandle.FromString("lifecycle-parent"), batchHandle);
		var child = CreateJob(JobHandle.FromString("lifecycle-child"), batchHandle) with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await graph.EnqueueBatchAsync(
			CreateBatch(batchHandle, 2),
			[parent, child],
			[new() { ChildJobHandle = child.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero }],
			cancellationToken
		).ConfigureAwait(false);

		var listed = await graph.QueryBatchesAsync(new() { State = BatchState.Executing }, cancellationToken)
			.ConfigureAwait(false);
		ConformanceAssert.Equal(batchHandle, ConformanceAssert.NotNull(listed.SingleOrDefault(), BatchLifecycleName,
			"the inserted batch must be listed").BatchHandle, BatchLifecycleName, "batch listing must preserve identity");
		var members = await graph.QueryBatchMembersAsync(batchHandle, new(), cancellationToken).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual(
			new[] { child.JobHandle.Value, parent.JobHandle.Value }.Order(StringComparer.Ordinal),
			members.Select(static member => member.JobHandle.Value).Order(StringComparer.Ordinal),
			BatchLifecycleName,
			"batch member projection must include every inserted job"
		);

		var acquiredParent = ConformanceAssert.NotNull(
			(await graph.AcquireDueJobsAsync(CreateRequest("lifecycle-worker", parent.JobName), cancellationToken)
				.ConfigureAwait(false)).SingleOrDefault(),
			BatchLifecycleName,
			"the root batch member must be acquirable"
		);
		ConformanceAssert.Equal(parent.JobHandle, acquiredParent.JobHandle, BatchLifecycleName, "the dependency root must run first");
		await graph.CompleteAsync(parent.JobHandle, acquiredParent.Attempt, "lifecycle-worker", cancellationToken).ConfigureAwait(false);
		var released = ConformanceAssert.NotNull(
			(await graph.QueryJobsAsync(new() { JobHandle = child.JobHandle }, cancellationToken).ConfigureAwait(false)).SingleOrDefault(),
			BatchLifecycleName,
			"the child must remain queryable"
		);
		ConformanceAssert.Equal(JobState.Pending, released.State, BatchLifecycleName, "successful parent completion must release the child");

		var acquiredChild = ConformanceAssert.NotNull(
			(await graph.AcquireDueJobsAsync(CreateRequest("lifecycle-worker", child.JobName), cancellationToken)
				.ConfigureAwait(false)).SingleOrDefault(),
			BatchLifecycleName,
			"the released child must be acquirable"
		);
		await graph.CompleteAsync(child.JobHandle, acquiredChild.Attempt, "lifecycle-worker", cancellationToken).ConfigureAwait(false);
		var status = ConformanceAssert.NotNull(
			await graph.GetBatchStatusAsync(BatchHandle.FromString("lifecycle-batch"), cancellationToken).ConfigureAwait(false),
			BatchLifecycleName,
			"the completed batch must have status"
		);
		ConformanceAssert.Equal(BatchState.Succeeded, status.State, BatchLifecycleName, "all-success batch state must be Succeeded");
		ConformanceAssert.Equal(2, status.Succeeded, BatchLifecycleName, "both members must be counted as successful");
		ConformanceAssert.Equal(0, status.Remaining, BatchLifecycleName, "a completed batch has no remaining members");
		var projected = ConformanceAssert.NotNull(
			await graph.GetBatchGraphAsync(BatchHandle.FromString("lifecycle-batch"), cancellationToken).ConfigureAwait(false),
			BatchLifecycleName,
			"the durable graph must be queryable"
		);
		ConformanceAssert.Equal(2, projected.Nodes.Count, BatchLifecycleName, "graph projection must contain both nodes");
		ConformanceAssert.Equal(1, projected.Edges.Count, BatchLifecycleName, "graph projection must contain the dependency");

		await graph.DeleteBatchAsync(BatchHandle.FromString("lifecycle-batch"), cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Null(
			await graph.GetBatchStatusAsync(BatchHandle.FromString("lifecycle-batch"), cancellationToken).ConfigureAwait(false),
			BatchLifecycleName,
			"deleting a terminal batch must remove its header"
		);
		ConformanceAssert.Null(
			await graph.GetJobStatusAsync(parent.JobHandle, cancellationToken).ConfigureAwait(false),
			BatchLifecycleName,
			"deleting a terminal batch must remove its members"
		);
		ConformanceAssert.Equal(
			0,
			(await graph.QueryJobExecutionsAsync(parent.JobHandle, new(), cancellationToken).ConfigureAwait(false)).Count,
			BatchLifecycleName,
			"deleting a terminal batch must remove member execution history"
		);
	}

	private static async ValueTask InvalidBatchRollsBackAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, InvalidBatchName);
		var member = CreateJob("invalid-member", batchHandle: "invalid-batch") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		_ = await ConformanceAssert.ThrowsAnyAsync(
			() => graph.EnqueueBatchAsync(
				CreateBatch("invalid-batch", 1),
				[member],
				[new() { ChildJobHandle = member.JobHandle, ParentJobHandle = JobHandle.FromString("missing-parent"), Delay = TimeSpan.Zero }],
				cancellationToken
			),
			InvalidBatchName,
			"an edge referencing a missing parent must reject the batch atomically"
		).ConfigureAwait(false);
		ConformanceAssert.Null(
			await graph.GetBatchStatusAsync(BatchHandle.FromString("invalid-batch"), cancellationToken).ConfigureAwait(false),
			InvalidBatchName,
			"a rejected batch must not leave a header"
		);
		ConformanceAssert.Null(
			await graph.GetJobStatusAsync(member.JobHandle, cancellationToken).ConfigureAwait(false),
			InvalidBatchName,
			"a rejected batch must not leave members"
		);
	}

	private static async ValueTask ConditionalTriggersAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, TriggerName);
		var parent = CreateJob("trigger-parent");
		var successChild = CreateWaitingJob("trigger-success", 1);
		var failureChild = CreateWaitingJob("trigger-failure", 1);
		var completeChild = CreateWaitingJob("trigger-complete", 1);
		await graph.EnqueueAsync(parent, cancellationToken).ConfigureAwait(false);
		await graph.EnqueueContinuationAsync(successChild,
			[new() { ChildJobHandle = successChild.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero }], cancellationToken).ConfigureAwait(false);
		await graph.EnqueueContinuationAsync(failureChild,
			[new() { ChildJobHandle = failureChild.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero, Trigger = ContinuationTrigger.Failure }],
			cancellationToken).ConfigureAwait(false);
		await graph.EnqueueContinuationAsync(completeChild,
			[new() { ChildJobHandle = completeChild.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero, Trigger = ContinuationTrigger.Complete }],
			cancellationToken).ConfigureAwait(false);
		var acquired = ConformanceAssert.NotNull(
			(await graph.AcquireDueJobsAsync(CreateRequest("trigger-worker", parent.JobName), cancellationToken)
				.ConfigureAwait(false)).SingleOrDefault(), TriggerName, "the trigger parent must be acquirable");
		await graph.CompleteAsync(parent.JobHandle, acquired.Attempt, "trigger-worker", cancellationToken).ConfigureAwait(false);

		ConformanceAssert.Equal(JobState.Pending,
			(await GetJobAsync(graph, successChild.JobHandle, TriggerName, cancellationToken).ConfigureAwait(false)).State,
			TriggerName, "success trigger must release after success");
		ConformanceAssert.Equal(JobState.Skipped,
			(await GetJobAsync(graph, failureChild.JobHandle, TriggerName, cancellationToken).ConfigureAwait(false)).State,
			TriggerName, "failure trigger must skip after success");
		ConformanceAssert.Equal(JobState.Pending,
			(await GetJobAsync(graph, completeChild.JobHandle, TriggerName, cancellationToken).ConfigureAwait(false)).State,
			TriggerName, "complete trigger must release after success");

		var failedParent = CreateJob("trigger-failed-parent");
		var successAfterFailure = CreateWaitingJob("trigger-success-after-failure", 1);
		var failureAfterFailure = CreateWaitingJob("trigger-failure-after-failure", 1);
		var completeAfterFailure = CreateWaitingJob("trigger-complete-after-failure", 1);
		await graph.EnqueueAsync(failedParent, cancellationToken).ConfigureAwait(false);
		await graph.EnqueueContinuationAsync(successAfterFailure,
			[new() { ChildJobHandle = successAfterFailure.JobHandle, ParentJobHandle = failedParent.JobHandle, Delay = TimeSpan.Zero }],
			cancellationToken).ConfigureAwait(false);
		await graph.EnqueueContinuationAsync(failureAfterFailure,
			[new()
			{
				ChildJobHandle = failureAfterFailure.JobHandle,
					ParentJobHandle = failedParent.JobHandle,
					Delay = TimeSpan.Zero,
				Trigger = ContinuationTrigger.Failure,
			}], cancellationToken).ConfigureAwait(false);
		await graph.EnqueueContinuationAsync(completeAfterFailure,
			[new()
			{
				ChildJobHandle = completeAfterFailure.JobHandle,
					ParentJobHandle = failedParent.JobHandle,
					Delay = TimeSpan.Zero,
				Trigger = ContinuationTrigger.Complete,
			}], cancellationToken).ConfigureAwait(false);
		var acquiredFailedParent = ConformanceAssert.NotNull(
			(await graph.AcquireDueJobsAsync(CreateRequest("trigger-failed-worker", failedParent.JobName), cancellationToken)
				.ConfigureAwait(false)).SingleOrDefault(), TriggerName, "the failed trigger parent must be acquirable");
		await graph.FailAsync(
			failedParent.JobHandle,
			acquiredFailedParent.Attempt,
			"trigger-failed-worker",
			"expected failure",
			nextRetryAt: null,
			cancellationToken
		).ConfigureAwait(false);

		ConformanceAssert.Equal(JobState.Skipped,
			(await GetJobAsync(graph, successAfterFailure.JobHandle, TriggerName, cancellationToken).ConfigureAwait(false)).State,
			TriggerName, "success trigger must skip after failure");
		ConformanceAssert.Equal(JobState.Pending,
			(await GetJobAsync(graph, failureAfterFailure.JobHandle, TriggerName, cancellationToken).ConfigureAwait(false)).State,
			TriggerName, "failure trigger must release after failure");
		ConformanceAssert.Equal(JobState.Pending,
			(await GetJobAsync(graph, completeAfterFailure.JobHandle, TriggerName, cancellationToken).ConfigureAwait(false)).State,
			TriggerName, "complete trigger must release after failure");

		foreach (var failureParentFails in new[] { false, true })
		{
			foreach (var successParentSettlesFirst in new[] { false, true })
			{
				var suffix = $"{(failureParentFails ? "fails" : "succeeds")}-{(successParentSettlesFirst ? "success-first" : "failure-first")}";
				var mixedSuccessParent = CreateJob($"trigger-mixed-success-parent-{suffix}");
				var mixedFailureParent = CreateJob($"trigger-mixed-failure-parent-{suffix}");
				var mixedChild = CreateWaitingJob($"trigger-mixed-child-{suffix}", 2);
				await graph.EnqueueAsync(mixedSuccessParent, cancellationToken).ConfigureAwait(false);
				await graph.EnqueueAsync(mixedFailureParent, cancellationToken).ConfigureAwait(false);
				await graph.EnqueueContinuationAsync(
					mixedChild,
					[
						new()
						{
							ChildJobHandle = mixedChild.JobHandle,
							ParentJobHandle = mixedSuccessParent.JobHandle,
							Delay = TimeSpan.Zero,
							Trigger = ContinuationTrigger.Success,
						},
						new()
						{
							ChildJobHandle = mixedChild.JobHandle,
							ParentJobHandle = mixedFailureParent.JobHandle,
							Delay = TimeSpan.Zero,
							Trigger = ContinuationTrigger.Failure,
						},
					],
					cancellationToken
				).ConfigureAwait(false);
				var mixedParents = await graph.AcquireDueJobsAsync(
					CreateRequest("trigger-mixed-worker", mixedSuccessParent.JobName, mixedFailureParent.JobName),
					cancellationToken
				).ConfigureAwait(false);
				ConformanceAssert.Equal(2, mixedParents.Count, TriggerName,
					"both mixed-trigger parents must be acquirable");

				async ValueTask SettleSuccessParentAsync() => await graph.CompleteAsync(
					mixedSuccessParent.JobHandle,
					1,
					"trigger-mixed-worker",
					cancellationToken
				).ConfigureAwait(false);

				async ValueTask SettleFailureParentAsync()
				{
					if (failureParentFails)
					{
						await graph.FailAsync(
							mixedFailureParent.JobHandle,
							1,
							"trigger-mixed-worker",
							"expected failure",
							nextRetryAt: null,
							cancellationToken
						).ConfigureAwait(false);
					}
					else
					{
						await graph.CompleteAsync(
							mixedFailureParent.JobHandle,
							1,
							"trigger-mixed-worker",
							cancellationToken
						).ConfigureAwait(false);
					}
				}

				if (successParentSettlesFirst)
					await SettleSuccessParentAsync().ConfigureAwait(false);
				else
					await SettleFailureParentAsync().ConfigureAwait(false);

				var waitingMixed = await GetJobAsync(graph, mixedChild.JobHandle, TriggerName, cancellationToken)
					.ConfigureAwait(false);
				ConformanceAssert.Equal(JobState.AwaitingContinuation, waitingMixed.State, TriggerName,
					"a mixed-trigger child must wait until both incoming edges settle");
				ConformanceAssert.Equal(1, waitingMixed.RemainingDependencies, TriggerName,
					"one mixed-trigger dependency must remain after the first parent settles");

				if (successParentSettlesFirst)
					await SettleFailureParentAsync().ConfigureAwait(false);
				else
					await SettleSuccessParentAsync().ConfigureAwait(false);

				var settledMixed = await GetJobAsync(graph, mixedChild.JobHandle, TriggerName, cancellationToken)
					.ConfigureAwait(false);
				ConformanceAssert.Equal(
					failureParentFails ? JobState.Pending : JobState.Skipped,
					settledMixed.State,
					TriggerName,
					"mixed triggers must evaluate every incoming edge independent of settlement order"
				);
				ConformanceAssert.Equal(failureParentFails ? 1 : 0, settledMixed.FailedDependencies, TriggerName,
					"mixed-trigger failure counts must reflect the final parent outcomes");
			}
		}
	}

	private static async ValueTask FanInAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, FanInName);
		var successfulParent = CreateJob("fanin-success-parent");
		var failedParent = CreateJob("fanin-failed-parent");
		var child = CreateWaitingJob("fanin-child", 2);
		await graph.EnqueueAsync(successfulParent, cancellationToken).ConfigureAwait(false);
		await graph.EnqueueAsync(failedParent, cancellationToken).ConfigureAwait(false);
		await graph.EnqueueContinuationAsync(
			child,
			[
				new() { ChildJobHandle = child.JobHandle, ParentJobHandle = successfulParent.JobHandle, Delay = TimeSpan.Zero, Trigger = ContinuationTrigger.Failure },
				new() { ChildJobHandle = child.JobHandle, ParentJobHandle = failedParent.JobHandle, Delay = TimeSpan.Zero, Trigger = ContinuationTrigger.Failure },
			],
			cancellationToken
		).ConfigureAwait(false);
		var acquired = await graph.AcquireDueJobsAsync(
			CreateRequest("fanin-worker", successfulParent.JobName, failedParent.JobName), cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Equal(2, acquired.Count, FanInName, "both dependency parents must be acquired");
		await graph.CompleteAsync(successfulParent.JobHandle, 1, "fanin-worker", cancellationToken).ConfigureAwait(false);
		var waiting = await GetJobAsync(graph, child.JobHandle, FanInName, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Equal(JobState.AwaitingContinuation, waiting.State, FanInName,
			"fan-in child must wait for every parent");
		ConformanceAssert.Equal(1, waiting.RemainingDependencies, FanInName,
			"one dependency must remain after the first parent settles");
		await graph.FailAsync(failedParent.JobHandle, 1, "fanin-worker", "expected failure", nextRetryAt: null, cancellationToken)
			.ConfigureAwait(false);
		var released = await GetJobAsync(graph, child.JobHandle, FanInName, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Equal(JobState.Pending, released.State, FanInName,
			"a failure-trigger fan-in must run when any parent fails after all settle");
		ConformanceAssert.Equal(1, released.FailedDependencies, FanInName,
			"failed-dependency projection must be preserved");
	}

	private static async ValueTask DynamicContinuationsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, DynamicName);
		var current = CreateJob("dynamic-current", batchHandle: "dynamic-batch");
		var waiter = CreateJob("dynamic-waiter", batchHandle: "dynamic-batch") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await graph.EnqueueBatchAsync(
			CreateBatch("dynamic-batch", 2),
			[current, waiter],
			[new() { ChildJobHandle = waiter.JobHandle, ParentJobHandle = current.JobHandle, Delay = TimeSpan.Zero }],
			cancellationToken
		).ConfigureAwait(false);
		_ = await graph.AcquireDueJobsAsync(CreateRequest("dynamic-worker", current.JobName), cancellationToken)
			.ConfigureAwait(false);
		var inserted = CreateJob("dynamic-inserted", batchHandle: "dynamic-batch");
		await graph.CompleteWithContinuationsAsync(
			current.JobHandle,
			1,
			"dynamic-worker",
			[new() { Job = inserted, Delay = TimeSpan.Zero, Options = ContinuationOptions.BeforeContinuations }],
			cancellationToken
		).ConfigureAwait(false);
		ConformanceAssert.Equal(JobState.Pending,
			(await GetJobAsync(graph, inserted.JobHandle, DynamicName, cancellationToken).ConfigureAwait(false)).State,
			DynamicName, "a before-continuations addition must become runnable");
		var stillWaiting = await GetJobAsync(graph, waiter.JobHandle, DynamicName, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Equal(JobState.AwaitingContinuation, stillWaiting.State, DynamicName,
			"existing waiters must be spliced behind the inserted continuation");
		var projection = ConformanceAssert.NotNull(
			await graph.GetBatchGraphAsync(BatchHandle.FromString("dynamic-batch"), cancellationToken).ConfigureAwait(false),
			DynamicName, "dynamic graph must remain queryable");
		ConformanceAssert.True(
			projection.Edges.Any(edge => edge.ParentJobHandle == inserted.JobHandle && edge.ChildJobHandle == waiter.JobHandle),
			DynamicName,
			"splicing must replace the existing waiter dependency atomically"
		);

		var stale = CreateJob("dynamic-stale", batchHandle: "dynamic-batch");
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => graph.AddBatchJobAsync(current.JobHandle, 1, stale, ContinuationOptions.BesideContinuations, cancellationToken),
			DynamicName,
			"a completed execution cannot add another batch member"
		).ConfigureAwait(false);
		ConformanceAssert.Null(await graph.GetJobStatusAsync(stale.JobHandle, cancellationToken).ConfigureAwait(false),
			DynamicName, "a rejected dynamic addition must not partially insert its job");
	}

	private static async ValueTask CancelUnsettledChainAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, CancellationName);
		var parent = CreateJob("cancel-parent", batchHandle: "cancel-batch");
		var child = CreateJob("cancel-child", batchHandle: "cancel-batch") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await graph.EnqueueBatchAsync(CreateBatch("cancel-batch", 2), [parent, child],
			[new() { ChildJobHandle = child.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero }], cancellationToken).ConfigureAwait(false);
		await graph.CancelBatchAsync(BatchHandle.FromString("cancel-batch"), cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Equal(JobState.Cancelled,
			ConformanceAssert.NotNull(await graph.GetJobStatusAsync(parent.JobHandle, cancellationToken).ConfigureAwait(false),
				CancellationName, "cancelled parent must remain observable").State,
			CancellationName, "batch cancellation must cancel a pending root");
		ConformanceAssert.Equal(JobState.Cancelled,
			ConformanceAssert.NotNull(await graph.GetJobStatusAsync(child.JobHandle, cancellationToken).ConfigureAwait(false),
				CancellationName, "cancelled child must remain observable").State,
			CancellationName, "batch cancellation must cancel an unresolved child");
		var status = ConformanceAssert.NotNull(
			await graph.GetBatchStatusAsync(BatchHandle.FromString("cancel-batch"), cancellationToken).ConfigureAwait(false),
			CancellationName, "cancelled batch status must remain observable");
		ConformanceAssert.Equal(BatchState.Cancelled, status.State, CancellationName,
			"a batch with only explicit cancellations must be Cancelled");
		ConformanceAssert.Equal(2, status.Cancelled, CancellationName,
			"every non-terminal member must be included in cancellation counters");
	}

	private static async ValueTask PurgeExpiredBatchAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, BatchPurgeName);
		var batchHandle = BatchHandle.FromString("purge-batch");
		var parent = CreateJob("purge-parent", batchHandle.Value);
		var child = CreateJob("purge-child", batchHandle.Value) with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await graph.EnqueueBatchAsync(
			CreateBatch(batchHandle, 2),
			[parent, child],
			[new() { ChildJobHandle = child.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero }],
			cancellationToken
		).ConfigureAwait(false);

		var acquiredParent = ConformanceAssert.NotNull(
			(await graph.AcquireDueJobsAsync(CreateRequest("purge-worker", parent.JobName), cancellationToken)
				.ConfigureAwait(false)).SingleOrDefault(),
			BatchPurgeName,
			"the parent must be acquirable"
		);
		await graph.CompleteAsync(parent.JobHandle, acquiredParent.Attempt, "purge-worker", cancellationToken)
			.ConfigureAwait(false);

		var acquiredChild = ConformanceAssert.NotNull(
			(await graph.AcquireDueJobsAsync(CreateRequest("purge-worker", child.JobName), cancellationToken)
				.ConfigureAwait(false)).SingleOrDefault(),
			BatchPurgeName,
			"the released child must be acquirable"
		);
		await graph.CompleteAsync(child.JobHandle, acquiredChild.Attempt, "purge-worker", cancellationToken)
			.ConfigureAwait(false);

		timeProvider.Advance(TimeSpan.FromHours(2));
		await graph.PurgeBatchesAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(1), cancellationToken)
			.ConfigureAwait(false);

		ConformanceAssert.Null(
			await graph.GetBatchStatusAsync(batchHandle, cancellationToken).ConfigureAwait(false),
			BatchPurgeName,
			"retention cleanup must remove the expired batch"
		);
		ConformanceAssert.Null(
			await graph.GetJobStatusAsync(parent.JobHandle, cancellationToken).ConfigureAwait(false),
			BatchPurgeName,
			"retention cleanup must remove the expired batch members"
		);
		ConformanceAssert.Null(
			await graph.GetJobStatusAsync(child.JobHandle, cancellationToken).ConfigureAwait(false),
			BatchPurgeName,
			"retention cleanup must remove continuation children in the expired batch"
		);
	}

	private static async ValueTask RejectsStaleActiveExecutionAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, StaleDynamicName);
		var clock = timeProvider;
		var current = CreateJob("stale-dynamic-parent", batchHandle: "stale-dynamic-batch");
		await graph.EnqueueBatchAsync(
			CreateBatch("stale-dynamic-batch", 1),
			[current],
			[],
			cancellationToken
		).ConfigureAwait(false);
		_ = await graph.AcquireDueJobsAsync(CreateRequest("stale-dynamic-worker", current.JobName), cancellationToken)
			.ConfigureAwait(false);
		clock.Advance(TimeSpan.FromMinutes(2));
		_ = await graph.AcquireDueJobsAsync(CreateRequest("stale-dynamic-worker", current.JobName), cancellationToken)
			.ConfigureAwait(false);

		var addition = CreateJob("stale-dynamic-child", batchHandle: "stale-dynamic-batch");
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => graph.AddBatchJobAsync(
				current.JobHandle,
				1,
				addition,
				ContinuationOptions.BesideContinuations,
				cancellationToken
			),
			StaleDynamicName,
			"a stale execution must not add a batch member while a newer execution is active"
		).ConfigureAwait(false);
		ConformanceAssert.Null(
			await graph.GetJobStatusAsync(addition.JobHandle, cancellationToken).ConfigureAwait(false),
			StaleDynamicName,
			"rejecting a stale execution must not partially insert its addition"
		);
		var active = await GetJobAsync(graph, current.JobHandle, StaleDynamicName, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Equal(JobState.Active, active.State, StaleDynamicName, "the newer execution must remain active");
		ConformanceAssert.Equal(2, active.Attempt, StaleDynamicName, "the newer execution ordinal must remain current");
	}

	private static async ValueTask EvaluatesAlreadyTerminalParentsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		_ = timeProvider;
		var graph = GetGraph(storage, TerminalParentName);
		var parent = CreateJob("terminal-parent");
		await graph.EnqueueAsync(parent, cancellationToken).ConfigureAwait(false);
		_ = await graph.AcquireDueJobsAsync(CreateRequest("terminal-parent-worker", parent.JobName), cancellationToken)
			.ConfigureAwait(false);
		await graph.FailAsync(
			parent.JobHandle,
			1,
			"terminal-parent-worker",
			"expected failure",
			nextRetryAt: null,
			cancellationToken
		).ConfigureAwait(false);

		foreach (var (trigger, expectedState) in new[]
		{
			(ContinuationTrigger.Success, JobState.Skipped),
			(ContinuationTrigger.Complete, JobState.Pending),
			(ContinuationTrigger.Failure, JobState.Pending),
		})
		{
			var child = CreateWaitingJob($"terminal-child-{trigger}", 1);
			await graph.EnqueueContinuationAsync(
				child,
				[new() { ChildJobHandle = child.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero, Trigger = trigger }],
				cancellationToken
			).ConfigureAwait(false);
			var persisted = await GetJobAsync(graph, child.JobHandle, TerminalParentName, cancellationToken).ConfigureAwait(false);
			ConformanceAssert.Equal(expectedState, persisted.State, TerminalParentName,
				"a continuation inserted after its parent settled must be evaluated immediately", $"trigger={trigger}");
			ConformanceAssert.Equal(1, persisted.FailedDependencies, TerminalParentName,
				"a late continuation must project the parent failure", $"trigger={trigger}");
		}
	}

	private static async ValueTask RejectsInvalidBatchRelationshipsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		_ = timeProvider;
		var graph = GetGraph(storage, InvalidDynamicName);
		var current = CreateJob("invalid-dynamic-parent", batchHandle: "invalid-dynamic-batch");
		await graph.EnqueueBatchAsync(
			CreateBatch("invalid-dynamic-batch", 1),
			[current],
			[],
			cancellationToken
		).ConfigureAwait(false);
		_ = await graph.AcquireDueJobsAsync(CreateRequest("invalid-dynamic-worker", current.JobName), cancellationToken)
			.ConfigureAwait(false);

		foreach (var options in new[] { ContinuationOptions.Detached, ContinuationOptions.BesideContinuations })
		{
			var addition = CreateJob($"invalid-dynamic-{options}", batchHandle: "other-batch");
			_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
				() => graph.CompleteWithContinuationsAsync(
					current.JobHandle,
					1,
					"invalid-dynamic-worker",
					[new() { Job = addition, Delay = TimeSpan.Zero, Options = options }],
					cancellationToken
				),
				InvalidDynamicName,
				"a dynamic continuation with an invalid batch relationship must be rejected",
				$"options={options}"
			).ConfigureAwait(false);
			ConformanceAssert.Null(await graph.GetJobStatusAsync(addition.JobHandle, cancellationToken).ConfigureAwait(false),
				InvalidDynamicName, "a rejected continuation must not be inserted", $"options={options}");
			var active = await GetJobAsync(graph, current.JobHandle, InvalidDynamicName, cancellationToken).ConfigureAwait(false);
			ConformanceAssert.Equal(JobState.Active, active.State, InvalidDynamicName,
				"a rejected continuation must not complete its current execution", $"options={options}");
		}

		var batch = ConformanceAssert.NotNull(
			await graph.GetBatchStatusAsync(BatchHandle.FromString("invalid-dynamic-batch"), cancellationToken).ConfigureAwait(false),
			InvalidDynamicName,
			"the original batch must remain observable"
		);
		ConformanceAssert.Equal(1, batch.Total, InvalidDynamicName,
			"rejected continuations must not change the original batch total");
	}

	private static IJobGraphStorage GetGraph(IJobStorage storage, string caseName) =>
		ConformanceAssert.IsAssignableFrom<IJobGraphStorage>(
			storage,
			caseName,
			"a storage advertising graph support must implement IJobGraphStorage"
		);

	private static JobRecord CreateJob(string id, string? batchHandle = null) =>
		new()
		{
			JobHandle = JobHandle.FromString(id),
			JobName = id,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = DateTimeOffset.UnixEpoch,
			CreatedAt = DateTimeOffset.UnixEpoch,
			BatchHandle = BatchHandle.FromString(batchHandle),
		};

	private static JobRecord CreateJob(JobHandle id, BatchHandle? batchHandle = null) =>
		CreateJob(id.Value, batchHandle?.Value);

	private static JobRecord CreateWaitingJob(string id, int dependencies) =>
		CreateJob(id) with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = dependencies,
		};

	private static BatchRecord CreateBatch(string id, int count) =>
		new()
		{
			BatchHandle = BatchHandle.FromString(id),
			CreatedAt = DateTimeOffset.UnixEpoch,
			TotalJobs = count,
			PendingCount = count,
			State = BatchState.Executing,
		};

	private static BatchRecord CreateBatch(BatchHandle id, int count) =>
		CreateBatch(id.Value, count);

	private static JobAcquisitionRequest CreateRequest(string workerId, params string[] jobNames) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = Math.Max(jobNames.Length, 1),
		Queues =
		[
			new()
			{
				QueueName = new JobRecord
				{
					JobHandle = JobHandle.FromString("default-queue-probe"),
					JobName = "default-queue-probe",
					Payload = "{}",
					State = JobState.Pending,
					DueAt = DateTimeOffset.UnixEpoch,
					CreatedAt = DateTimeOffset.UnixEpoch,
				}.QueueName,
				Capacity = Math.Max(jobNames.Length, 1),
				JobCapacities = jobNames.Distinct(StringComparer.Ordinal)
					.ToDictionary(static name => name, static _ => 1, StringComparer.Ordinal),
			},
		],
	};

	private static async ValueTask<JobRecord> GetJobAsync(
		IJobStorage storage,
		string id,
		string caseName,
		CancellationToken cancellationToken
	) => ConformanceAssert.NotNull(
		(await storage.QueryJobsAsync(new() { JobHandle = JobHandle.FromString(id) }, cancellationToken).ConfigureAwait(false)).SingleOrDefault(),
		caseName,
		"the expected graph job must exist",
		$"jobHandle={id}"
	);

	private static ValueTask<JobRecord> GetJobAsync(
		IJobStorage storage,
		JobHandle id,
		string caseName,
		CancellationToken cancellationToken
	) => GetJobAsync(storage, id.Value, caseName, cancellationToken);
}

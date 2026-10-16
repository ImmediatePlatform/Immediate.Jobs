using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static partial class GraphStorageConformance
{
	private const string ConcurrentSpliceName = "Graph.Dynamic.ConcurrentSplicePreservesOriginalParentAndTrigger";
	private const string BufferedSpliceName = "Graph.Dynamic.BufferedSpliceWaitsForEveryAddition";
	private const string CyclicGraphName = "Graph.Validation.RejectsCyclesWithoutPartialWrites";
	private const string DuplicateEdgeName = "Graph.Validation.RejectsDuplicateLogicalEdgesWithoutPartialWrites";
	private const string MemberDeletionName = "Graph.Maintenance.RejectsIndividualBatchMemberDeletion";

	private static async ValueTask ConcurrentSplicePreservesParentsAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var graph = GetGraph(storage, ConcurrentSpliceName);
		await storage.MergeJobDefinitionsListAsync(new()
		{
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
			Definitions = [new() { Name = "splice-parent" }, new() { Name = "splice-waiter" }, new() { Name = "splice-inserted" }],
		}, token);

		foreach (var trigger in new[] { ContinuationTrigger.Success, ContinuationTrigger.Failure })
		{
			foreach (var parentSucceeds in new[] { true, false })
			{
				foreach (var insertedSettlesFirst in new[] { true, false })
				{
					var context = $"trigger={trigger}; parentSucceeds={parentSucceeds}; insertedSettlesFirst={insertedSettlesFirst}";
					var batchHandle = BatchHandle.FromString(context);
					var parent = CreateJob(context + "-parent", batchHandle) with { JobName = "splice-parent" };
					var waiter = CreateJob(context + "-waiter", batchHandle) with
					{
						JobName = "splice-waiter",
						State = JobState.AwaitingContinuation,
						RemainingDependencies = 1,
					};
					var originalEdge = new JobContinuationEdge
					{
						ParentJobHandle = parent.JobHandle,
						ChildJobHandle = waiter.JobHandle,
						Trigger = trigger,
						Delay = TimeSpan.Zero,
					};
					await graph.EnqueueBatchAsync(CreateBatch(batchHandle, 2), [parent, waiter], [originalEdge], token);
					var activeParent = (await storage.AcquireDueJobsAsync(CreateRequest("splice-worker", parent.JobName), token)).Single();
					var inserted = CreateJob(context + "-inserted", batchHandle) with { JobName = "splice-inserted" };
					await graph.AddBatchJobAsync(parent.JobHandle, activeParent.Attempt, inserted, ContinuationOptions.BeforeContinuations, token);
					var activeInserted = (await storage.AcquireDueJobsAsync(CreateRequest("splice-worker", inserted.JobName), token)).Single();

					async ValueTask SettleParentAsync()
					{
						if (parentSucceeds)
							await storage.CompleteAsync(parent.JobHandle, activeParent.Attempt, "splice-worker", token);
						else
							await storage.FailAsync(parent.JobHandle, activeParent.Attempt, "splice-worker", "parent failed", nextRetryAt: null, token);
					}

					if (insertedSettlesFirst)
						await storage.CompleteAsync(inserted.JobHandle, activeInserted.Attempt, "splice-worker", token);
					else
						await SettleParentAsync();

					var waiting = await GetJobAsync(storage, waiter.JobHandle, ConcurrentSpliceName, token);
					ConformanceAssert.Equal(JobState.AwaitingContinuation, waiting.State, ConcurrentSpliceName,
						"the waiter must keep waiting until both the original and inserted parents settle", context);
					ConformanceAssert.Equal(1, waiting.RemainingDependencies, ConcurrentSpliceName,
						"settling either parent must leave the other dependency intact", context);
					var dependencies = (await storage.GetJobStatusAsync(waiter.JobHandle, token))!.DependsOn;
					ConformanceAssert.True(dependencies.Contains(originalEdge), ConcurrentSpliceName,
						"splicing must retain the original edge and its trigger", context);
					ConformanceAssert.True(dependencies.Any(edge => edge.ParentJobHandle == inserted.JobHandle && edge.Trigger == ContinuationTrigger.Success),
						ConcurrentSpliceName, "inserted work adds its own success dependency", context);

					if (insertedSettlesFirst)
						await SettleParentAsync();
					else
						await storage.CompleteAsync(inserted.JobHandle, activeInserted.Attempt, "splice-worker", token);

					var shouldRun = trigger == ContinuationTrigger.Success ? parentSucceeds : !parentSucceeds;
					var released = await GetJobAsync(storage, waiter.JobHandle, ConcurrentSpliceName, token);
					ConformanceAssert.Equal(shouldRun ? JobState.Pending : JobState.Skipped, released.State, ConcurrentSpliceName,
						"the original trigger must still govern release after the inserted job succeeds", context);
					ConformanceAssert.Equal(0, released.RemainingDependencies, ConcurrentSpliceName, "both dependencies must settle exactly once", context);
				}
			}
		}
	}

	private static async ValueTask BufferedSplicePreservesEveryAdditionAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var graph = GetGraph(storage, BufferedSpliceName);
		await storage.MergeJobDefinitionsListAsync(new()
		{
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
			Definitions = [new() { Name = "buffered-parent" }, new() { Name = "buffered-waiter" },
				new() { Name = "buffered-first" }, new() { Name = "buffered-second" }],
		}, token);
		var batch = BatchHandle.FromString("buffered-splice");
		var parent = CreateJob("buffered-parent", batch);
		var waiter = CreateJob("buffered-waiter", batch) with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		await graph.EnqueueBatchAsync(CreateBatch(batch, 2), [parent, waiter],
			[new() { ParentJobHandle = parent.JobHandle, ChildJobHandle = waiter.JobHandle, Delay = TimeSpan.Zero }], token);
		var active = (await storage.AcquireDueJobsAsync(CreateRequest("buffered-worker", parent.JobName), token)).Single();
		var first = CreateJob("buffered-first", batch);
		var second = CreateJob("buffered-second", batch);
		await graph.CompleteWithContinuationsAsync(parent.JobHandle, active.Attempt, "buffered-worker",
			[new() { Job = first, Options = ContinuationOptions.BeforeContinuations, Delay = TimeSpan.Zero },
			 new() { Job = second, Options = ContinuationOptions.BeforeContinuations, Delay = TimeSpan.Zero }], token);

		var acquired = await storage.AcquireDueJobsAsync(CreateRequest("buffered-worker", first.JobName, second.JobName), token);
		ConformanceAssert.SequenceEqual(new[] { first.JobHandle, second.JobHandle }.OrderBy(handle => handle.Value, StringComparer.Ordinal),
			acquired.Select(job => job.JobHandle).OrderBy(handle => handle.Value, StringComparer.Ordinal), BufferedSpliceName,
			"buffered additions must become independent runnable siblings rather than an accidental chain");
		await storage.CompleteAsync(first.JobHandle, acquired.Single(job => job.JobHandle == first.JobHandle).Attempt, "buffered-worker", token);
		var waiting = await GetJobAsync(storage, waiter.JobHandle, BufferedSpliceName, token);
		ConformanceAssert.Equal(JobState.AwaitingContinuation, waiting.State, BufferedSpliceName, "one successful addition must not release the waiter");
		ConformanceAssert.Equal(1, waiting.RemainingDependencies, BufferedSpliceName, "the other buffered addition remains a dependency");
		await storage.CompleteAsync(second.JobHandle, acquired.Single(job => job.JobHandle == second.JobHandle).Attempt, "buffered-worker", token);
		ConformanceAssert.Equal(JobState.Pending, (await GetJobAsync(storage, waiter.JobHandle, BufferedSpliceName, token)).State,
			BufferedSpliceName, "the waiter releases after every buffered addition succeeds");
		var status = (await graph.GetBatchStatusAsync(batch, token))!;
		ConformanceAssert.Equal((4, 3, 1), (status.Total, status.Succeeded, status.Remaining), BufferedSpliceName,
			"batch counters must include both additions exactly once");
	}

	private static async ValueTask RejectsCyclicGraphsAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var graph = GetGraph(storage, CyclicGraphName);
		await storage.MergeJobDefinitionsListAsync(new() { RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase), Definitions = [new() { Name = "cycle-a" }, new() { Name = "cycle-b" }] }, token);
		foreach (var selfCycle in new[] { false, true })
		{
			var batch = BatchHandle.FromString(selfCycle ? "self-cycle" : "two-node-cycle");
			var a = CreateJob("cycle-a", batch) with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
			var b = CreateJob("cycle-b", batch) with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
			JobSubmission[] members = selfCycle ? [a] : [a, b];
			JobContinuationEdge[] edges = selfCycle
				? [new() { ParentJobHandle = a.JobHandle, ChildJobHandle = a.JobHandle, Delay = TimeSpan.Zero }]
				: [new() { ParentJobHandle = a.JobHandle, ChildJobHandle = b.JobHandle, Delay = TimeSpan.Zero },
				   new() { ParentJobHandle = b.JobHandle, ChildJobHandle = a.JobHandle, Delay = TimeSpan.Zero }];
			await ConformanceAssert.ThrowsAsync<ImmediateJobException>(() => graph.EnqueueBatchAsync(CreateBatch(batch, members.Length), members, edges, token),
				CyclicGraphName, "self dependencies and multi-node cycles must be rejected", batch.Value);
			ConformanceAssert.Null(await graph.GetBatchStatusAsync(batch, token), CyclicGraphName, "rejected cycles must not leave a batch header");
			ConformanceAssert.Equal(0, (await storage.QueryJobsAsync(new(), token)).Count, CyclicGraphName, "rejected cycles must not leave jobs");
		}
	}

	private static async ValueTask RejectsDuplicateEdgesAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var graph = GetGraph(storage, DuplicateEdgeName);
		await storage.MergeJobDefinitionsListAsync(new() { RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase), Definitions = [new() { Name = "duplicate-parent" }, new() { Name = "duplicate-child" }] }, token);
		var parent = CreateJob("duplicate-parent");
		var child = CreateWaitingJob("duplicate-child", 2);
		await storage.EnqueueAsync(parent, token);
		var edge = new JobContinuationEdge { ParentJobHandle = parent.JobHandle, ChildJobHandle = child.JobHandle, Delay = TimeSpan.Zero };
		foreach (var duplicate in new[] { edge, edge with { Trigger = ContinuationTrigger.Complete, Delay = TimeSpan.FromMinutes(1) } })
		{
			await ConformanceAssert.ThrowsAsync<ImmediateJobException>(() => graph.EnqueueContinuationAsync(child, [edge, duplicate], token),
				DuplicateEdgeName, "one logical parent-child relationship cannot be inserted twice even with different metadata");
			ConformanceAssert.Null(await storage.GetJobStatusAsync(child.JobHandle, token), DuplicateEdgeName, "a rejected child must not be persisted");
			ConformanceAssert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(parent.JobHandle, token))!.State,
				DuplicateEdgeName, "rejecting a child must not change its existing parent");
		}

		await graph.EnqueueContinuationAsync(child with { RemainingDependencies = 1 }, [edge], token);
		var accepted = (await storage.GetJobStatusAsync(child.JobHandle, token))!;
		ConformanceAssert.Equal(1, accepted.DependsOn.Count, DuplicateEdgeName,
			"failed inserts must not leak edges into a later valid continuation using the same handle");
	}

	private static async ValueTask RejectsIndividualBatchMemberDeletionAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var graph = GetGraph(storage, MemberDeletionName);
		await storage.MergeJobDefinitionsListAsync(new() { RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase), Definitions = [new() { Name = "delete-parent" }, new() { Name = "delete-child" }] }, token);
		var batch = BatchHandle.FromString("delete-member-batch");
		var parent = CreateJob("delete-parent", batch);
		var child = CreateJob("delete-child", batch) with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		await graph.EnqueueBatchAsync(CreateBatch(batch, 2), [parent, child],
			[new() { ParentJobHandle = parent.JobHandle, ChildJobHandle = child.JobHandle, Delay = TimeSpan.Zero }], token);
		var active = (await storage.AcquireDueJobsAsync(CreateRequest("delete-worker", parent.JobName), token)).Single();
		await storage.CompleteAsync(parent.JobHandle, active.Attempt, "delete-worker", token);
		var status = await graph.GetBatchStatusAsync(batch, token);
		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(() => storage.DeleteAsync(parent.JobHandle, token), MemberDeletionName,
			"a terminal member cannot be deleted separately from its batch");
		ConformanceAssert.Equal(JobState.Succeeded, (await storage.GetJobStatusAsync(parent.JobHandle, token))!.State,
			MemberDeletionName, "a rejected deletion must preserve the member");
		ConformanceAssert.Equal(status, await graph.GetBatchStatusAsync(batch, token), MemberDeletionName, "a rejected deletion must preserve batch progress");
		var persisted = (await graph.GetBatchGraphAsync(batch, token))!;
		ConformanceAssert.Equal((2, 1), (persisted.Nodes.Count, persisted.Edges.Count), MemberDeletionName, "a rejected deletion must preserve the graph");
	}
}

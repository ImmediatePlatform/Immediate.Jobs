using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryContinuationIndexTests
{
	[Fact]
	public async Task LateContinuationDoesNotResettleExistingDependencies()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var done = Job("done") with { State = JobState.Succeeded, CompletedAt = clock.GetUtcNow() };
		var pending = Job("pending", "other");
		var waiting = Job("waiting") with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		JobContinuationEdge[] existing = [Edge(done.JobHandle, waiting.JobHandle), Edge(pending.JobHandle, waiting.JobHandle)];
		storage.LoadPersistedJobState([done, pending, waiting], [], existing, []);

		var late = Submission("late");
		await storage.EnqueueContinuationAsync(late, [Edge(done.JobHandle, late.JobHandle)], token);

		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(late.JobHandle, token))!.State);
		Assert.Equal(1, Assert.Single(await storage.QueryJobsAsync(new() { JobHandle = waiting.JobHandle }, token)).RemainingDependencies);
		Assert.Equal(existing, (await storage.GetJobStatusAsync(waiting.JobHandle, token))!.DependsOn);
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(Request("other"), token));
		await storage.CompleteAsync(acquired.JobHandle, acquired.Attempt, "worker", token);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(waiting.JobHandle, token))!.State);
	}

	[Fact]
	public async Task RecursiveSkippingSettlesMixedJobAndBatchDependenciesOnce()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var batch = BatchHandle.FromString("batch");
		var root = Job("root");
		var member = Job("member") with { BatchHandle = batch, State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		var child = Job("child") with { State = JobState.AwaitingContinuation, RemainingDependencies = 3 };
		var descendant = Job("descendant") with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		JobContinuationEdge[] edges =
		[
			Edge(root.JobHandle, member.JobHandle),
			Edge(root.JobHandle, child.JobHandle),
			Edge(member.JobHandle, child.JobHandle),
			new() { ParentBatchHandle = batch, ChildJobHandle = child.JobHandle, Delay = TimeSpan.Zero },
			Edge(child.JobHandle, descendant.JobHandle),
		];
		storage.LoadPersistedJobState([root, member, child, descendant], [Batch(batch)], edges, []);
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(Request(), token));

		await storage.FailAsync(acquired.JobHandle, acquired.Attempt, "worker", "failed", nextRetryAt: null, token);

		foreach (var handle in new[] { member.JobHandle, child.JobHandle, descendant.JobHandle })
			Assert.Equal(JobState.Skipped, (await storage.GetJobStatusAsync(handle, token))!.State);
		var settledChild = Assert.Single(await storage.QueryJobsAsync(new() { JobHandle = child.JobHandle }, token));
		Assert.Equal(0, settledChild.RemainingDependencies);
		Assert.Equal(1, settledChild.FailedDependencies);
		Assert.Equal(3, (await storage.GetJobStatusAsync(child.JobHandle, token))!.DependsOn.Count);
		var status = await storage.GetBatchStatusAsync(batch, token);
		Assert.NotNull(status);
		Assert.Equal(0, status.Remaining);
		Assert.Equal(1, status.Skipped);
	}

	[Fact]
	public async Task LateBatchPropagatesMixedTerminalParentsWithoutDoubleSettlement()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		await RegisterAsync(storage, token);
		var parentBatch = BatchHandle.FromString("parent");
		var failed = Job("parent") with { State = JobState.Failed, CompletedAt = DateTimeOffset.UnixEpoch };
		var member = Job("member") with { BatchHandle = parentBatch, State = JobState.Succeeded, CompletedAt = DateTimeOffset.UnixEpoch };
		storage.LoadPersistedJobState([failed, member],
			[Batch(parentBatch) with { State = BatchState.Succeeded, PendingCount = 0, SucceededCount = 1, CompletedAt = DateTimeOffset.UnixEpoch }], [], []);
		var batch = BatchHandle.FromString("new-batch");
		var first = Submission("first") with { BatchHandle = batch };
		var second = Submission("second") with { BatchHandle = batch };
		var third = Submission("third") with { BatchHandle = batch };
		JobContinuationEdge[] edges =
		[
			Edge(failed.JobHandle, first.JobHandle),
			Edge(first.JobHandle, second.JobHandle),
			new() { ParentBatchHandle = parentBatch, ChildJobHandle = second.JobHandle, Delay = TimeSpan.Zero },
			Edge(second.JobHandle, third.JobHandle) with { Trigger = ContinuationTrigger.Complete },
		];

		await storage.EnqueueBatchAsync(Batch(batch, 3), [first, second, third], edges, token);

		Assert.Equal(JobState.Skipped, (await storage.GetJobStatusAsync(first.JobHandle, token))!.State);
		Assert.Equal(JobState.Skipped, (await storage.GetJobStatusAsync(second.JobHandle, token))!.State);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(third.JobHandle, token))!.State);
		var status = await storage.GetBatchStatusAsync(batch, token);
		Assert.NotNull(status);
		Assert.Equal(2, status.Skipped);
		Assert.Equal(1, status.Remaining);
		Assert.Equal(edges, (await storage.GetBatchGraphAsync(batch, token))!.Edges);
	}

	[Fact]
	public async Task SplicedWaitersRemainBlockedUntilTheInsertedParentCompletes()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		await RegisterAsync(storage, token);
		var batch = BatchHandle.FromString("batch");
		var root = Job("root") with { BatchHandle = batch };
		var waiter = Job("waiter") with { BatchHandle = batch, State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		storage.LoadPersistedJobState([root, waiter], [Batch(batch, 2)], [Edge(root.JobHandle, waiter.JobHandle)], []);
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(Request(), token));
		var inserted = Submission("inserted") with { BatchHandle = batch };

		await storage.CompleteWithContinuationsAsync(root.JobHandle, acquired.Attempt, "worker",
			[new() { Job = inserted, Options = ContinuationOptions.BeforeContinuations, Delay = TimeSpan.Zero }], token);

		Assert.Equal(JobState.AwaitingContinuation, (await storage.GetJobStatusAsync(waiter.JobHandle, token))!.State);
		Assert.Equal([root.JobHandle, inserted.JobHandle], (await storage.GetJobStatusAsync(waiter.JobHandle, token))!.DependsOn.Select(static edge => edge.ParentJobHandle));
		acquired = Assert.Single(await storage.AcquireDueJobsAsync(Request(), token));
		Assert.Equal(inserted.JobHandle, acquired.JobHandle);
		await storage.CompleteAsync(acquired.JobHandle, acquired.Attempt, "worker", token);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(waiter.JobHandle, token))!.State);
		Assert.Equal(3, (await storage.GetBatchGraphAsync(batch, token))!.Edges.Count);
	}

	[Fact]
	public async Task JobPurgeRemovesIncomingAndOutgoingEdgesBeforeHandleReuse()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(10));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var now = clock.GetUtcNow();
		var removedParent = Job("removed-parent") with { State = JobState.Succeeded, CompletedAt = now.AddDays(-2) };
		var removedChild = removedParent with { JobHandle = JobHandle.FromString("removed-child") };
		var retainedParent = Job("retained-parent") with { State = JobState.Succeeded, CompletedAt = now };
		var retainedChild = retainedParent with { JobHandle = JobHandle.FromString("retained-child") };
		var sibling = retainedParent with { JobHandle = JobHandle.FromString("sibling") };
		storage.LoadPersistedJobState([removedParent, removedChild, retainedParent, retainedChild, sibling], [],
			[Edge(removedParent.JobHandle, retainedChild.JobHandle), Edge(retainedParent.JobHandle, removedChild.JobHandle), Edge(retainedParent.JobHandle, sibling.JobHandle)], []);

		await storage.PurgeJobsAsync(TimeSpan.FromDays(1), TimeSpan.FromDays(1), token);

		Assert.Empty((await storage.GetJobStatusAsync(retainedChild.JobHandle, token))!.DependsOn);
		Assert.Single((await storage.GetJobStatusAsync(sibling.JobHandle, token))!.DependsOn);
		await storage.EnqueueContinuationAsync(Submission(removedChild.JobHandle.Value), [Edge(retainedParent.JobHandle, removedChild.JobHandle)], token);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(removedChild.JobHandle, token))!.State);
		await storage.EnqueueAsync(Submission(removedParent.JobHandle.Value) with { JobName = "other" }, token);
		var child = Submission("new-child");
		await storage.EnqueueContinuationAsync(child, [Edge(removedParent.JobHandle, child.JobHandle)], token);
		Assert.Equal(JobState.AwaitingContinuation, (await storage.GetJobStatusAsync(child.JobHandle, token))!.State);
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(Request("other"), token));
		await storage.CompleteAsync(acquired.JobHandle, acquired.Attempt, "worker", token);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(child.JobHandle, token))!.State);
		Assert.Empty((await storage.GetJobStatusAsync(retainedChild.JobHandle, token))!.DependsOn);
	}

	[Fact]
	public async Task BatchPurgeRemovesEdgesBeforeReusingBatchAndMemberHandles()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(10));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var now = clock.GetUtcNow();
		var batch = BatchHandle.FromString("batch");
		var parent = Job("parent") with { State = JobState.Succeeded, CompletedAt = now };
		var member = Job("member") with { BatchHandle = batch, State = JobState.Succeeded, CompletedAt = now.AddDays(-2) };
		var child = Job("child") with { State = JobState.Succeeded, CompletedAt = now };
		JobContinuationEdge batchEdge = new() { ParentBatchHandle = batch, ChildJobHandle = child.JobHandle, Delay = TimeSpan.Zero };
		storage.LoadPersistedJobState([parent, member, child],
			[Batch(batch) with { State = BatchState.Succeeded, PendingCount = 0, SucceededCount = 1, CompletedAt = now.AddDays(-2) }],
			[Edge(parent.JobHandle, member.JobHandle), batchEdge], []);

		await storage.PurgeBatchesAsync(TimeSpan.FromDays(1), TimeSpan.FromDays(1), token);

		Assert.Empty((await storage.GetJobStatusAsync(child.JobHandle, token))!.DependsOn);
		var newMember = Submission(member.JobHandle.Value) with { BatchHandle = batch };
		await storage.EnqueueBatchAsync(Batch(batch), [newMember], [Edge(parent.JobHandle, member.JobHandle)], token);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(member.JobHandle, token))!.State);
		var waiting = Submission("waiting");
		await storage.EnqueueContinuationAsync(waiting, [batchEdge with { ChildJobHandle = waiting.JobHandle }], token);
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(Request(), token));
		Assert.Equal(member.JobHandle, acquired.JobHandle);
		await storage.CompleteAsync(acquired.JobHandle, acquired.Attempt, "worker", token);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(waiting.JobHandle, token))!.State);
		Assert.Empty((await storage.GetJobStatusAsync(child.JobHandle, token))!.DependsOn);
		var late = Submission("late");
		await storage.EnqueueContinuationAsync(late, [batchEdge with { ChildJobHandle = late.JobHandle }], token);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(late.JobHandle, token))!.State);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RecurringDependenciesAreIndexedForLiveAndTerminalParents(bool terminal)
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var parent = Job("parent") with { State = terminal ? JobState.Succeeded : JobState.Pending };
		storage.LoadPersistedJobState([parent], [], [], []);
		var schedule = new RecurringJobSchedule
		{
			Name = "schedule",
			JobName = "work",
			Cron = "* * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = clock.GetUtcNow(),
		};
		await storage.UpsertRecurringAsync(schedule, token);
		var child = Job("occurrence") with { State = JobState.AwaitingContinuation, RemainingDependencies = 1, RecurringKey = "occurrence" };
		JobContinuationEdge[] edges = [Edge(parent.JobHandle, child.JobHandle)];

		Assert.True(await storage.MaterializeRecurringAsync(schedule, child, clock.GetUtcNow().AddMinutes(1), edges, token));
		if (!terminal)
		{
			var acquired = Assert.Single(await storage.AcquireDueJobsAsync(Request(), token));
			await storage.CompleteAsync(acquired.JobHandle, acquired.Attempt, "worker", token);
		}

		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(child.JobHandle, token))!.State);
		Assert.Equal(edges, (await storage.GetJobStatusAsync(child.JobHandle, token))!.DependsOn);
		Assert.False(await storage.MaterializeRecurringAsync(schedule, child, clock.GetUtcNow().AddMinutes(1), edges, token));
		Assert.Single((await storage.GetJobStatusAsync(child.JobHandle, token))!.DependsOn);
	}

	private static ValueTask RegisterAsync(InMemoryJobStorage storage, CancellationToken token) => storage.MergeJobDefinitionsListAsync(new()
	{
		Definitions = [new() { Name = "work" }, new() { Name = "other" }],
		RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
	}, token);

	private static JobRecord Job(string id, string name = "work") => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		CreatedAt = DateTimeOffset.UnixEpoch,
		DueAt = DateTimeOffset.UnixEpoch,
	};

	private static JobSubmission Submission(string id) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = "work",
		Payload = "{}",
		State = JobState.Pending,
		CreatedAt = DateTimeOffset.UnixEpoch,
		DueAt = DateTimeOffset.UnixEpoch,
	};

	private static JobContinuationEdge Edge(JobHandle parent, JobHandle child) => new()
	{
		ParentJobHandle = parent,
		ChildJobHandle = child,
		Delay = TimeSpan.Zero,
	};

	private static BatchRecord Batch(BatchHandle handle, int count = 1) => new()
	{
		BatchHandle = handle,
		State = BatchState.Executing,
		TotalJobs = count,
		PendingCount = count,
		CreatedAt = DateTimeOffset.UnixEpoch,
	};

	private static JobAcquisitionRequest Request(string name = "work") => new()
	{
		WorkerId = "worker",
		BatchSize = 1,
		Lease = TimeSpan.FromMinutes(1),
		Queues = [new() { QueueName = "default", Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [name] = 1 } }],
	};
}

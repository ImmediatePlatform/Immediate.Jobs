using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class InMemoryJobStorageBatchTests
{
	[Fact]
	public async Task BatchChainReleasesTransactionallyAndMaintainsProgress()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var parent = CreateJob("parent", batchId: "batch");
		var child = CreateJob("child", batchId: "batch") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(
			CreateBatch("batch", 2),
			[parent, child],
			[new() { ChildJobId = child.Id, ParentJobId = parent.Id }],
			cancellationToken
		);

		var acquiredParent = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		Assert.Equal(parent.Id, acquiredParent.Id);
		Assert.Equal(DateTimeOffset.UnixEpoch, (await storage.GetBatchStatusAsync("batch", cancellationToken))!.StartedAt);

		await storage.CompleteAsync(parent.Id, "worker", cancellationToken);
		var waitingReleased = await GetJobAsync(storage, child.Id, cancellationToken);
		Assert.Equal(JobState.Pending, waitingReleased.State);
		Assert.Equal(0, waitingReleased.RemainingDependencies);
		var running = await storage.GetBatchStatusAsync("batch", cancellationToken);
		Assert.Equal(1, running!.Succeeded);
		Assert.Equal(1, running.Remaining);

		var acquiredChild = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		Assert.Equal(child.Id, acquiredChild.Id);
		await storage.CompleteAsync(child.Id, "worker", cancellationToken);

		var completed = await storage.GetBatchStatusAsync("batch", cancellationToken);
		Assert.Equal(BatchState.Succeeded, completed!.State);
		Assert.Equal(2, completed.Succeeded);
		Assert.Equal(0, completed.Remaining);
		Assert.Equal(1, completed.FractionSettled);
		var graph = await storage.GetBatchGraphAsync("batch", cancellationToken);
		Assert.Equal(2, graph!.Nodes.Count);
		_ = Assert.Single(graph.Edges);
	}

	[Fact]
	public async Task TerminalParentIsEvaluatedWhenContinuationIsInserted()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var parent = CreateJob("parent");
		await storage.EnqueueAsync(parent, cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		await storage.FailAsync(parent.Id, "worker", "broken", nextRetryAt: null, cancellationToken);

		var successOnly = CreateJob("success-only") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueContinuationAsync(
			successOnly,
			[new() { ChildJobId = successOnly.Id, ParentJobId = parent.Id }],
			cancellationToken
		);
		Assert.Equal(JobState.Cancelled, (await GetJobAsync(storage, successOnly.Id, cancellationToken)).State);

		var always = CreateJob("always") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueContinuationAsync(
			always,
			[new()
			{
				ChildJobId = always.Id,
				ParentJobId = parent.Id,
				Trigger = ContinuationTrigger.Complete,
			}],
			cancellationToken
		);
		Assert.Equal(JobState.Pending, (await GetJobAsync(storage, always.Id, cancellationToken)).State);

		var failureOnly = CreateJob("failure-only") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueContinuationAsync(
			failureOnly,
			[new()
			{
				ChildJobId = failureOnly.Id,
				ParentJobId = parent.Id,
				Trigger = ContinuationTrigger.Failure,
			}],
			cancellationToken
		);
		var released = await GetJobAsync(storage, failureOnly.Id, cancellationToken);
		Assert.Equal(JobState.Pending, released.State);
		Assert.Equal(1, released.FailedDependencies);
	}

	[Fact]
	public async Task FailureContinuationIsCancelledWhenEveryParentSucceeds()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var parent = CreateJob("parent");
		var child = CreateJob("child") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueAsync(parent, cancellationToken);
		await storage.EnqueueContinuationAsync(
			child,
			[new()
			{
				ChildJobId = child.Id,
				ParentJobId = parent.Id,
				Trigger = ContinuationTrigger.Failure,
			}],
			cancellationToken
		);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));

		await storage.CompleteAsync(parent.Id, "worker", cancellationToken);

		Assert.Equal(JobState.Cancelled, (await GetJobAsync(storage, child.Id, cancellationToken)).State);
	}

	[Fact]
	public async Task FailureFanInWaitsForAllParentsAndRunsWhenAnyParentFails()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var successfulParent = CreateJob("parent") with { Id = "successful-parent" };
		var failedParent = CreateJob("parent") with { Id = "failed-parent" };
		var child = CreateJob("child") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 2,
		};
		await storage.EnqueueAsync(successfulParent, cancellationToken);
		await storage.EnqueueAsync(failedParent, cancellationToken);
		await storage.EnqueueContinuationAsync(
			child,
			[
				new()
				{
					ChildJobId = child.Id,
					ParentJobId = successfulParent.Id,
					Trigger = ContinuationTrigger.Failure,
				},
				new()
				{
					ChildJobId = child.Id,
					ParentJobId = failedParent.Id,
					Trigger = ContinuationTrigger.Failure,
				},
			],
			cancellationToken
		);
		var parents = await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken);
		Assert.Equal(2, parents.Count);

		await storage.CompleteAsync(successfulParent.Id, "worker", cancellationToken);

		var waiting = await GetJobAsync(storage, child.Id, cancellationToken);
		Assert.Equal(JobState.AwaitingContinuation, waiting.State);
		Assert.Equal(1, waiting.RemainingDependencies);
		Assert.Equal(0, waiting.FailedDependencies);

		await storage.FailAsync(failedParent.Id, "worker", "broken", nextRetryAt: null, cancellationToken);

		var released = await GetJobAsync(storage, child.Id, cancellationToken);
		Assert.Equal(JobState.Pending, released.State);
		Assert.Equal(0, released.RemainingDependencies);
		Assert.Equal(1, released.FailedDependencies);
	}

	[Fact]
	public async Task InvalidBatchDoesNotPartiallyInsertAnything()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var member = CreateJob("member", batchId: "batch") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};

		_ = await Assert.ThrowsAsync<KeyNotFoundException>(() => storage.EnqueueBatchAsync(
			CreateBatch("batch", 1),
			[member],
			[new() { ChildJobId = member.Id, ParentJobId = "missing" }],
			cancellationToken
		).AsTask());

		Assert.Null(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new(), cancellationToken));
	}

	[Fact]
	public async Task SuccessfulDynamicContinuationSplicesExistingWaiters()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var current = CreateJob("parent", batchId: "batch");
		var waiter = CreateJob("child", batchId: "batch") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(
			CreateBatch("batch", 2),
			[current, waiter],
			[new() { ChildJobId = waiter.Id, ParentJobId = current.Id }],
			cancellationToken
		);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		var inserted = CreateJob("inserted", batchId: "batch");

		await storage.CompleteWithContinuationsAsync(
			current.Id,
			"worker",
			[new() { Job = inserted, Options = ContinuationOptions.BeforeContinuations }],
			cancellationToken
		);

		Assert.Equal(JobState.Pending, (await GetJobAsync(storage, inserted.Id, cancellationToken)).State);
		var stillWaiting = await GetJobAsync(storage, waiter.Id, cancellationToken);
		Assert.Equal(JobState.AwaitingContinuation, stillWaiting.State);
		Assert.Equal(1, stillWaiting.RemainingDependencies);
		var status = await storage.GetBatchStatusAsync("batch", cancellationToken);
		Assert.Equal(3, status!.Total);
		Assert.Equal(2, status.Remaining);
		var graph = await storage.GetBatchGraphAsync("batch", cancellationToken);
		Assert.Contains(graph!.Edges, edge => edge.ParentJobId == inserted.Id && edge.ChildJobId == waiter.Id);
	}

	[Fact]
	public async Task BatchCanBeListedCancelledAndDeletedAsOneUnit()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var parent = CreateJob("cancel-parent", batchId: "cancel-batch");
		var child = CreateJob("cancel-child", batchId: "cancel-batch") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(
			CreateBatch("cancel-batch", 2),
			[parent, child],
			[new() { ChildJobId = child.Id, ParentJobId = parent.Id }],
			cancellationToken
		);

		var listed = Assert.Single(await storage.QueryBatchesAsync(new(), cancellationToken));
		Assert.Equal("cancel-batch", listed.Id);
		await storage.CancelBatchAsync("cancel-batch", cancellationToken);
		var cancelled = await storage.GetBatchStatusAsync("cancel-batch", cancellationToken);
		Assert.Equal(BatchState.Cancelled, cancelled!.State);
		Assert.Equal(2, cancelled.Cancelled);

		await storage.DeleteBatchAsync("cancel-batch", cancellationToken);
		Assert.Null(await storage.GetBatchStatusAsync("cancel-batch", cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new(), cancellationToken));
	}

	private static JobRecord CreateJob(string id, string? batchId = null) => new()
	{
		Id = id,
		JobName = id,
		Payload = "{}",
		State = JobState.Pending,
		DueAt = DateTimeOffset.UnixEpoch,
		CreatedAt = DateTimeOffset.UnixEpoch,
		BatchId = batchId,
	};

	private static JobBatchRecord CreateBatch(string id, int count) => new()
	{
		Id = id,
		CreatedAt = DateTimeOffset.UnixEpoch,
		TotalJobs = count,
		PendingCount = count,
		State = BatchState.Executing,
	};

	private static JobAcquisitionRequest CreateRequest(string workerId) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = 10,
		Queues =
		[
			new()
			{
				QueueName = JobQueueDefinition.DefaultName,
				Capacity = 10,
				JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal)
				{
					["parent"] = 10,
					["child"] = 10,
					["always"] = 10,
					["success-only"] = 10,
					["inserted"] = 10,
				},
			},
		],
	};

	private static async ValueTask<JobRecord> GetJobAsync(
		InMemoryJobStorage storage,
		string jobId,
		CancellationToken cancellationToken
	) => Assert.Single(await storage.QueryJobsAsync(new() { Id = jobId }, cancellationToken));
}
#pragma warning restore CS1591

using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class InMemoryJobStorageBatchTests
{
	[Fact]
	public async Task BulkIncomingEdgesHandlesEmptyAndDuplicateIdsAndPreservesEdges()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var batchParent = CreateJob("batch-parent", batchId: "parent-batch");
		var jobParent = CreateJob("job-parent");
		var child = CreateJob("child") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 2,
		};
		await storage.EnqueueBatchAsync(
			CreateBatch("parent-batch", 1),
			[batchParent],
			[],
			cancellationToken
		);
		await storage.EnqueueAsync(jobParent, cancellationToken);
		await storage.EnqueueContinuationAsync(
			child,
			[
				new()
				{
					ChildJobId = child.JobId,
					ParentJobId = jobParent.JobId,
					Trigger = ContinuationTrigger.Failure,
				},
				new()
				{
					ChildJobId = child.JobId,
					ParentBatchId = "parent-batch",
					Trigger = ContinuationTrigger.Complete,
				},
			],
			cancellationToken
		);

		Assert.Empty(await storage.GetIncomingEdgesAsync([], cancellationToken));
		var edges = await storage.GetIncomingEdgesAsync([child.JobId, child.JobId, "missing"], cancellationToken);
		Assert.Equal(2, edges.Count);
		Assert.Contains(edges, edge => string.Equals(edge.ChildJobId, child.JobId, StringComparison.Ordinal) && string.Equals(edge.ParentJobId, jobParent.JobId, StringComparison.Ordinal) &&
			edge.ParentBatchId is null &&
			edge.Trigger == ContinuationTrigger.Failure);
		Assert.Contains(edges, edge => string.Equals(edge.ChildJobId, child.JobId, StringComparison.Ordinal) && edge.ParentJobId is null && string.Equals(edge.ParentBatchId, "parent-batch", StringComparison.Ordinal) &&
			edge.Trigger == ContinuationTrigger.Complete);
		_ = await Assert.ThrowsAsync<ArgumentNullException>(
			() => storage.GetIncomingEdgesAsync(null!, cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ArgumentException>(
			() => storage.GetIncomingEdgesAsync([" "], cancellationToken).AsTask()
		);
	}

	[Fact]
	public async Task LowLevelStatusReportsUnknownMaxAttempts()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var job = CreateJob("job");
		await storage.EnqueueAsync(job, cancellationToken);

		Assert.Null((await storage.GetJobStatusAsync(job.JobId, cancellationToken))!.MaxAttempts);
	}

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
			[new() { ChildJobId = child.JobId, ParentJobId = parent.JobId }],
			cancellationToken
		);

		var acquiredParent = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		Assert.Equal(parent.JobId, acquiredParent.JobId);
		Assert.Equal(DateTimeOffset.UnixEpoch, (await storage.GetBatchStatusAsync("batch", cancellationToken))!.StartedAt);

		await storage.CompleteAsync(parent.JobId, 1, "worker", cancellationToken);
		var waitingReleased = await GetJobAsync(storage, child.JobId, cancellationToken);
		Assert.Equal(JobState.Pending, waitingReleased.State);
		Assert.Equal(0, waitingReleased.RemainingDependencies);
		var running = await storage.GetBatchStatusAsync("batch", cancellationToken);
		Assert.Equal(1, running!.Succeeded);
		Assert.Equal(1, running.Remaining);

		var acquiredChild = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		Assert.Equal(child.JobId, acquiredChild.JobId);
		await storage.CompleteAsync(child.JobId, 1, "worker", cancellationToken);

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
	public async Task AddingBatchJobReportsStaleAndActiveExecutionNumbers()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var current = CreateJob("parent", batchId: "batch");
		await storage.EnqueueBatchAsync(CreateBatch("batch", 1), [current], [], cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		clock.Advance(TimeSpan.FromMinutes(2));
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));

		var exception = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.AddBatchJobAsync(
			current.JobId,
			1,
			CreateJob("child", batchId: "batch"),
			ContinuationOptions.BesideContinuations,
			cancellationToken
		).AsTask());

		Assert.Contains("Execution 1", exception.Message, StringComparison.Ordinal);
		Assert.Contains("active execution is 2", exception.Message, StringComparison.Ordinal);
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
		await storage.FailAsync(parent.JobId, 1, "worker", "broken", nextRetryAt: null, cancellationToken);

		var successOnly = CreateJob("success-only") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueContinuationAsync(
			successOnly,
			[new() { ChildJobId = successOnly.JobId, ParentJobId = parent.JobId }],
			cancellationToken
		);
		Assert.Equal(JobState.Skipped, (await GetJobAsync(storage, successOnly.JobId, cancellationToken)).State);

		var always = CreateJob("always") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueContinuationAsync(
			always,
			[new()
			{
				ChildJobId = always.JobId,
				ParentJobId = parent.JobId,
				Trigger = ContinuationTrigger.Complete,
			}],
			cancellationToken
		);
		Assert.Equal(JobState.Pending, (await GetJobAsync(storage, always.JobId, cancellationToken)).State);

		var failureOnly = CreateJob("failure-only") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueContinuationAsync(
			failureOnly,
			[new()
			{
				ChildJobId = failureOnly.JobId,
				ParentJobId = parent.JobId,
				Trigger = ContinuationTrigger.Failure,
			}],
			cancellationToken
		);
		var released = await GetJobAsync(storage, failureOnly.JobId, cancellationToken);
		Assert.Equal(JobState.Pending, released.State);
		Assert.Equal(1, released.FailedDependencies);
	}

	[Fact]
	public async Task FailureContinuationIsSkippedWhenEveryParentSucceeds()
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
				ChildJobId = child.JobId,
				ParentJobId = parent.JobId,
				Trigger = ContinuationTrigger.Failure,
			}],
			cancellationToken
		);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));

		await storage.CompleteAsync(parent.JobId, 1, "worker", cancellationToken);

		Assert.Equal(JobState.Skipped, (await GetJobAsync(storage, child.JobId, cancellationToken)).State);
	}

	[Fact]
	public async Task FailureFanInWaitsForAllParentsAndRunsWhenAnyParentFails()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var successfulParent = CreateJob("parent") with { JobId = "successful-parent" };
		var failedParent = CreateJob("parent") with { JobId = "failed-parent" };
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
					ChildJobId = child.JobId,
					ParentJobId = successfulParent.JobId,
					Trigger = ContinuationTrigger.Failure,
				},
				new()
				{
					ChildJobId = child.JobId,
					ParentJobId = failedParent.JobId,
					Trigger = ContinuationTrigger.Failure,
				},
			],
			cancellationToken
		);
		var parents = await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken);
		Assert.Equal(2, parents.Count);

		await storage.CompleteAsync(successfulParent.JobId, 1, "worker", cancellationToken);

		var waiting = await GetJobAsync(storage, child.JobId, cancellationToken);
		Assert.Equal(JobState.AwaitingContinuation, waiting.State);
		Assert.Equal(1, waiting.RemainingDependencies);
		Assert.Equal(0, waiting.FailedDependencies);

		await storage.FailAsync(failedParent.JobId, 1, "worker", "broken", nextRetryAt: null, cancellationToken);

		var released = await GetJobAsync(storage, child.JobId, cancellationToken);
		Assert.Equal(JobState.Pending, released.State);
		Assert.Equal(0, released.RemainingDependencies);
		Assert.Equal(1, released.FailedDependencies);
	}

	[Theory]
	[InlineData(false, false, JobState.Skipped)]
	[InlineData(false, true, JobState.Skipped)]
	[InlineData(true, false, JobState.Pending)]
	[InlineData(true, true, JobState.Pending)]
	public async Task MixedTriggersUseAllIncomingEdgesRegardlessOfCompletionOrder(
		bool failureParentFails,
		bool successParentSettlesFirst,
		JobState expectedState
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var successParent = CreateJob("parent") with { JobId = "success-parent" };
		var failureParent = CreateJob("parent") with { JobId = "failure-parent" };
		var child = CreateJob("child") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 2,
		};
		await storage.EnqueueAsync(successParent, cancellationToken);
		await storage.EnqueueAsync(failureParent, cancellationToken);
		await storage.EnqueueContinuationAsync(
			child,
			[
				new()
				{
					ChildJobId = child.JobId,
					ParentJobId = successParent.JobId,
					Trigger = ContinuationTrigger.Success,
				},
				new()
				{
					ChildJobId = child.JobId,
					ParentJobId = failureParent.JobId,
					Trigger = ContinuationTrigger.Failure,
				},
			],
			cancellationToken
		);
		var parents = await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken);
		Assert.Equal(2, parents.Count);

		if (successParentSettlesFirst)
		{
			await storage.CompleteAsync(successParent.JobId, 1, "worker", cancellationToken);
			await SettleFailureParentAsync();
		}
		else
		{
			await SettleFailureParentAsync();
			await storage.CompleteAsync(successParent.JobId, 1, "worker", cancellationToken);
		}

		var settled = await GetJobAsync(storage, child.JobId, cancellationToken);
		Assert.Equal(expectedState, settled.State);
		Assert.Equal(0, settled.RemainingDependencies);
		Assert.Equal(failureParentFails ? 1 : 0, settled.FailedDependencies);

		async ValueTask SettleFailureParentAsync()
		{
			if (failureParentFails)
				await storage.FailAsync(failureParent.JobId, 1, "worker", "broken", nextRetryAt: null, cancellationToken);
			else
				await storage.CompleteAsync(failureParent.JobId, 1, "worker", cancellationToken);
		}
	}

	[Theory]
	[InlineData(ContinuationOptions.Detached, "other-batch")]
	[InlineData(ContinuationOptions.BesideContinuations, "other-batch")]
	public async Task DynamicContinuationRejectsInvalidBatchRelationship(
		ContinuationOptions options,
		string additionBatchId
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var current = CreateJob("parent", batchId: "batch");
		await storage.EnqueueBatchAsync(CreateBatch("batch", 1), [current], [], cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		var addition = CreateJob("inserted", batchId: additionBatchId);

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.JobId,
			1, "worker",
			[new() { Job = addition, Options = options }],
			cancellationToken
		).AsTask());

		Assert.Empty(await storage.QueryJobsAsync(new() { Id = addition.JobId }, cancellationToken));
		Assert.Equal(JobState.Active, (await GetJobAsync(storage, current.JobId, cancellationToken)).State);
		Assert.Equal(1, (await storage.GetBatchStatusAsync("batch", cancellationToken))!.Total);
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
			[new() { ChildJobId = member.JobId, ParentJobId = "missing" }],
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
			[new() { ChildJobId = waiter.JobId, ParentJobId = current.JobId }],
			cancellationToken
		);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		var inserted = CreateJob("inserted", batchId: "batch");

		await storage.CompleteWithContinuationsAsync(
			current.JobId,
			1, "worker",
			[new() { Job = inserted, Options = ContinuationOptions.BeforeContinuations }],
			cancellationToken
		);

		Assert.Equal(JobState.Pending, (await GetJobAsync(storage, inserted.JobId, cancellationToken)).State);
		var stillWaiting = await GetJobAsync(storage, waiter.JobId, cancellationToken);
		Assert.Equal(JobState.AwaitingContinuation, stillWaiting.State);
		Assert.Equal(1, stillWaiting.RemainingDependencies);
		var status = await storage.GetBatchStatusAsync("batch", cancellationToken);
		Assert.Equal(3, status!.Total);
		Assert.Equal(2, status.Remaining);
		var graph = await storage.GetBatchGraphAsync("batch", cancellationToken);
		Assert.Contains(graph!.Edges, edge => string.Equals(edge.ParentJobId, inserted.JobId, StringComparison.Ordinal) && string.Equals(edge.ChildJobId, waiter.JobId, StringComparison.Ordinal));
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
			[new() { ChildJobId = child.JobId, ParentJobId = parent.JobId }],
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
		JobId = id,
		JobName = id,
		Payload = "{}",
		State = JobState.Pending,
		DueAt = DateTimeOffset.UnixEpoch,
		CreatedAt = DateTimeOffset.UnixEpoch,
		BatchId = batchId,
	};

	private static BatchRecord CreateBatch(string id, int count) => new()
	{
		BatchId = id,
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

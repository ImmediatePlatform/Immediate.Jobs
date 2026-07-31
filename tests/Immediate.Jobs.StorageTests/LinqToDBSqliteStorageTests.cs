using System.Globalization;
using Immediate.Jobs.LinqToDB;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

#pragma warning disable CS1591
public sealed class LinqToDBSqliteStorageTests
{
	[Fact]
	public async Task SchemaBootstrapIsIdempotentAndRejectsNamedSqliteSchema()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		await fixture.Options.CreateImmediateJobsSchemaAsync(cancellationToken: cancellationToken);
		await fixture.Options.CreateImmediateJobsSchemaAsync(cancellationToken: cancellationToken);
		var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			fixture.Options.CreateImmediateJobsSchemaAsync("jobs", cancellationToken)
		);
		Assert.Equal("schema", exception.ParamName);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("{\"usage\":{\"userId\":\"42\"}}")]
	public async Task ContextAndLeaseRecoveryRoundTrip(string? context)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with
		{
			Context = context,
			GroupId = "tenant-a",
		};
		await storage.EnqueueAsync(job, cancellationToken);

		var queried = Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken));
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		Assert.Equal(context, queried.Context);
		Assert.Equal(context, acquired.Context);
		Assert.Equal("tenant-a", queried.GroupId);
		Assert.Equal("tenant-a", acquired.GroupId);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
		var recovered = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-b", recovered.WorkerId);
		Assert.Equal("tenant-a", recovered.GroupId);
	}

	[Fact]
	public async Task BulkIncomingEdgesPreserveFieldsAndNormalizeInput()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var batchParent = CreateJob(now, 0) with
		{
			Id = "batch-parent-member",
			BatchId = "parent-batch",
			State = JobState.Succeeded,
			CompletedAt = now,
		};
		await storage.EnqueueBatchAsync(new()
		{
			Id = "parent-batch",
			CreatedAt = now,
			TotalJobs = 1,
			PendingCount = 0,
			SucceededCount = 1,
			State = BatchState.Succeeded,
		}, [batchParent], [], cancellationToken);
		var jobParent = CreateJob(now, 1) with { Id = "job-parent" };
		await storage.EnqueueAsync(jobParent, cancellationToken);
		var child = CreateJob(now, 2) with { Id = "child" };
		await storage.EnqueueContinuationAsync(child,
		[
			new() { ChildJobId = child.Id, ParentJobId = jobParent.Id, Trigger = ContinuationTrigger.Success },
			new() { ChildJobId = child.Id, ParentBatchId = "parent-batch", Trigger = ContinuationTrigger.Complete },
		], cancellationToken);

		var edges = await storage.GetIncomingEdgesAsync([child.Id, child.Id, "missing"], cancellationToken);

		Assert.Collection(edges,
			edge =>
			{
				Assert.Equal(child.Id, edge.ChildJobId);
				Assert.Equal(jobParent.Id, edge.ParentJobId);
				Assert.Null(edge.ParentBatchId);
				Assert.Equal(ContinuationTrigger.Success, edge.Trigger);
			},
			edge =>
			{
				Assert.Equal(child.Id, edge.ChildJobId);
				Assert.Null(edge.ParentJobId);
				Assert.Equal("parent-batch", edge.ParentBatchId);
				Assert.Equal(ContinuationTrigger.Complete, edge.Trigger);
			});
		Assert.Empty(await storage.GetIncomingEdgesAsync([], cancellationToken));
		_ = await Assert.ThrowsAsync<ArgumentException>(() =>
			storage.GetIncomingEdgesAsync([" "], cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			storage.GetIncomingEdgesAsync(null!, cancellationToken).AsTask()
		);
	}

	[Fact]
	public async Task LowLevelStatusReportsUnknownMaxAttempts()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 0);
		await storage.EnqueueAsync(job, cancellationToken);

		var status = Assert.IsType<JobStatus>(await storage.GetJobStatusAsync(job.Id, cancellationToken));

		Assert.Null(status.MaxAttempts);
	}

	[Fact]
	public async Task MissingDashboardActionsThrowKeyNotFound()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();

		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.RetryAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.DeleteAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.RemoveRecurringAsync("missing", cancellationToken).AsTask()
		);
	}

	[Fact]
	public async Task DashboardActionsRejectExistingResourcesInInvalidStates()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 0) with { Id = "pending" }, cancellationToken);
		await storage.UpsertRecurringAsync(new()
		{
			Name = "code-defined",
			JobName = "storage-test",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		}, cancellationToken);

		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.RetryAsync("pending", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.DeleteAsync("pending", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.RemoveRecurringAsync("code-defined", cancellationToken).AsTask()
		);
	}

	[Fact]
	public async Task EmptyBatchIsFullySettled()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		await using (var connection = new DataConnection(fixture.Options))
		{
			_ = await connection.ExecuteAsync(
				$"""
				INSERT INTO "immediate_job_batches"
				("Id", "CreatedAt", "TotalJobs", "PendingCount", "SucceededCount", "FailedCount", "CancelledCount", "SkippedCount", "State", "ConcurrencyStamp")
				VALUES ('empty', 0, 0, 0, 0, 0, 0, 0, {(int)BatchState.Succeeded}, '{Guid.NewGuid()}')
				""",
				cancellationToken
			);
		}

		var status = Assert.IsType<BatchStatus>(await fixture.CreateStorage().GetBatchStatusAsync("empty", cancellationToken));

		Assert.Equal(1d, status.FractionSettled);
	}

	[Fact]
	public async Task FairQueuesRotateGroupsWhenCapacityIsOneAndTheSecondGroupArrivesLater()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with
		{
			Id = "group-a-first",
			GroupId = "group-a",
		}, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with
		{
			Id = "group-a-second",
			GroupId = "group-a",
		}, cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-a", 1),
			cancellationToken
		));
		await storage.CompleteAsync(first.Id, "worker-a", cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with
		{
			Id = "group-b-first",
			GroupId = "group-b",
		}, cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-b", 1),
			cancellationToken
		));

		Assert.Equal("group-a-first", first.Id);
		Assert.Equal("group-b-first", second.Id);
	}

	[Fact]
	public async Task FairQueuesInterleaveGroupsWithinOneBatch()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-1", 0, "a", cancellationToken);
		await Enqueue(storage, now, "a-2", 1, "a", cancellationToken);
		await Enqueue(storage, now, "b-1", 2, "b", cancellationToken);
		await Enqueue(storage, now, "b-2", 3, "b", cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(CreateFairRequest("worker", 4), cancellationToken);

		Assert.Equal(["a-1", "b-1", "a-2", "b-2"], acquired.Select(static job => job.Id));
	}

	[Fact]
	public async Task DisabledFairQueuesRetainExistingOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-1", 0, "a", cancellationToken);
		await Enqueue(storage, now, "a-2", 1, "a", cancellationToken);
		await Enqueue(storage, now, "b-1", 2, "b", cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(CreateRequest("worker", 2), cancellationToken);

		Assert.Equal(["a-1", "a-2"], acquired.Select(static job => job.Id));
	}

	[Fact]
	public async Task EnabledFairQueuesWithoutGroupedJobsRetainExistingOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "newer", 2, groupId: null, cancellationToken);
		await Enqueue(storage, now, "oldest", 0, groupId: null, cancellationToken);
		await Enqueue(storage, now, "middle", 1, groupId: null, cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(CreateFairRequest("worker", 3), cancellationToken);

		Assert.Equal(["oldest", "middle", "newer"], acquired.Select(static job => job.Id));
	}

	[Fact]
	public async Task NoisyGroupIsServedAfterQuietGroup()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "noisy-active-1", 0, "noisy", cancellationToken);
		await Enqueue(storage, now, "noisy-active-2", 1, "noisy", cancellationToken);
		_ = await storage.AcquireJobsAsync(
			["noisy-active-1", "noisy-active-2"],
			"existing-worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		);
		await Enqueue(storage, now, "noisy-waiting", 2, "noisy", cancellationToken);
		await Enqueue(storage, now, "quiet-waiting", 3, "quiet", cancellationToken);

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker", 1, new(0.50, 2, GroupRoundRobin: true)),
			cancellationToken
		));

		Assert.Equal("quiet-waiting", acquired.Id);
	}

	[Fact]
	public async Task ExpiredLeasesDoNotMakeAGroupNoisy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "expired-1", 0, "formerly-noisy", cancellationToken, "inactive");
		await Enqueue(storage, now, "expired-2", 1, "formerly-noisy", cancellationToken, "inactive");
		_ = await storage.AcquireJobsAsync(
			["expired-1", "expired-2"],
			"expired-worker",
			TimeSpan.FromSeconds(1),
			cancellationToken
		);
		await Enqueue(storage, now, "formerly-noisy", 2, "formerly-noisy", cancellationToken);
		await Enqueue(storage, now, "quiet", 3, "quiet", cancellationToken);
		fixture.TimeProvider.Advance(TimeSpan.FromSeconds(2));

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker", 1, new(0.50, 2, GroupRoundRobin: true)),
			cancellationToken
		));

		Assert.Equal("formerly-noisy", acquired.Id);
	}

	[Fact]
	public async Task DisablingRoundRobinUsesDueOrderAfterNoisyClassification()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-1", 0, "a", cancellationToken);
		await Enqueue(storage, now, "a-2", 1, "a", cancellationToken);
		await Enqueue(storage, now, "b-1", 2, "b", cancellationToken);
		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-a", 1),
			cancellationToken
		));
		await storage.CompleteAsync(first.Id, "worker-a", cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-b", 1, new(0.10, 30, GroupRoundRobin: false)),
			cancellationToken
		));

		Assert.Equal("a-2", second.Id);
	}

	[Fact]
	public async Task FairAcquisitionPreservesUngroupedOrderAndCapacities()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "ungrouped-limited", 0, groupId: null, cancellationToken, "limited");
		await Enqueue(storage, now, "grouped-limited", 1, "a", cancellationToken, "limited");
		await Enqueue(storage, now, "grouped-other-1", 2, "b", cancellationToken, "other");
		await Enqueue(storage, now, "grouped-other-2", 3, "c", cancellationToken, "other");
		var request = CreateFairRequest("worker", 4) with
		{
			Queues =
			[
				new()
				{
					QueueName = JobQueueDefinition.DefaultName,
					Capacity = 2,
					JobCapacities = new Dictionary<string, int> { ["limited"] = 1, ["other"] = 10 },
				},
			],
		};

		var acquired = await storage.AcquireDueJobsAsync(request, cancellationToken);

		Assert.Equal(["ungrouped-limited", "grouped-other-1"], acquired.Select(static job => job.Id));
	}

	[Fact]
	public async Task CursorResetsAfterGroupBacklogClears()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-first", 0, "a", cancellationToken);
		await Enqueue(storage, now, "a-second", 1, "a", cancellationToken);
		await Enqueue(storage, now, "b-active", 2, "b", cancellationToken);
		await Enqueue(storage, now, "b-waiting", 3, "b", cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-1", 1),
			cancellationToken
		));
		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-2", 1),
			cancellationToken
		));
		await storage.CompleteAsync(first.Id, "worker-1", cancellationToken);
		var third = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-3", 1),
			cancellationToken
		));
		await storage.CompleteAsync(third.Id, "worker-3", cancellationToken);
		await Enqueue(storage, now, "a-returned", 4, "a", cancellationToken);

		var afterReset = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-4", 1),
			cancellationToken
		));

		Assert.Equal("a-first", first.Id);
		Assert.Equal("b-active", second.Id);
		Assert.Equal("a-second", third.Id);
		Assert.Equal("a-returned", afterReset.Id);
	}

	[Fact]
	public async Task ReturningGroupCursorResetCommitsWithGroupedEnqueue()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		await InsertCursorAsync(fixture.Options, "returning", 42, cancellationToken);

		await fixture.CreateStorage().EnqueueAsync(CreateJob(fixture.TimeProvider.GetUtcNow(), 0) with
		{
			GroupId = "returning",
		}, cancellationToken);

		Assert.Equal(0, await DeleteCursorAsync(fixture.Options, "returning", cancellationToken));
	}

	[Fact]
	public async Task FailedGroupedEnqueueRollsBackReturningGroupCursorReset()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var duplicate = CreateJob(fixture.TimeProvider.GetUtcNow(), 0) with
		{
			Id = "duplicate",
			GroupId = "other",
		};
		await storage.EnqueueAsync(duplicate, cancellationToken);
		await InsertCursorAsync(fixture.Options, "returning", 42, cancellationToken);

		_ = await Assert.ThrowsAnyAsync<Exception>(() =>
			storage.EnqueueAsync(duplicate with { GroupId = "returning" }, cancellationToken).AsTask()
		);

		Assert.Equal(1, await DeleteCursorAsync(fixture.Options, "returning", cancellationToken));
	}

	[Fact]
	public async Task ConcurrentFairClaimsDoNotDuplicateJobs()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		foreach (var index in Enumerable.Range(0, 12))
			await Enqueue(first, now, string.Create(CultureInfo.InvariantCulture, $"job-{index}"), index, string.Create(CultureInfo.InvariantCulture, $"group-{index % 3}"), cancellationToken);

		var claims = await Task.WhenAll(
			first.AcquireDueJobsAsync(CreateFairRequest("worker-a", 12), cancellationToken).AsTask(),
			second.AcquireDueJobsAsync(CreateFairRequest("worker-b", 12), cancellationToken).AsTask()
		).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
		var acquired = claims.SelectMany(static jobs => jobs).ToArray();

		Assert.Equal(12, acquired.Length);
		Assert.Equal(12, acquired.Select(static job => job.Id).Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task DuplicateCursorSequencesConvergeWithoutStarvingAnUnservedGroup()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-first", 0, "a", cancellationToken);
		await Enqueue(storage, now, "b-first", 1, "b", cancellationToken);
		await Enqueue(storage, now, "a-next", 2, "a", cancellationToken);
		await Enqueue(storage, now, "b-next", 3, "b", cancellationToken);

		var firstPass = await storage.AcquireDueJobsAsync(CreateFairRequest("worker-1", 2), cancellationToken);
		Assert.Equal(["a-first", "b-first"], firstPass.Select(static job => job.Id));
		foreach (var job in firstPass)
			await storage.CompleteAsync(job.Id, "worker-1", cancellationToken);

		await using (var connection = new DataConnection(fixture.Options))
		{
			var updated = await connection.ExecuteAsync(
				"""
				UPDATE "immediate_fair_queue_groups"
				SET "LastServedSequence" = 5
				WHERE "QueueName" = 'default'
				""",
				cancellationToken
			);
			Assert.Equal(2, updated);
		}

		await Enqueue(storage, now, "c-first", 4, "c", cancellationToken);

		var secondPass = await storage.AcquireDueJobsAsync(CreateFairRequest("worker-2", 3), cancellationToken);

		Assert.Equal(["c-first", "a-next", "b-next"], secondPass.Select(static job => job.Id));
	}

	[Fact]
	public async Task CursorCleanupFailureDoesNotRollBackACompletedJob()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "grouped", 0, "a", cancellationToken);
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker", 1),
			cancellationToken
		));

		await using (var connection = new DataConnection(fixture.Options))
			_ = await connection.ExecuteAsync("DROP TABLE \"immediate_fair_queue_groups\"", cancellationToken);

		await storage.CompleteAsync(acquired.Id, "worker", cancellationToken);

		var completed = Assert.Single(await storage.QueryJobsAsync(new() { Id = acquired.Id }, cancellationToken));
		Assert.Equal(JobState.Succeeded, completed.State);
	}

	[Theory]
	[InlineData(ContinuationTrigger.Success, true, JobState.Pending)]
	[InlineData(ContinuationTrigger.Success, false, JobState.Skipped)]
	[InlineData(ContinuationTrigger.Failure, true, JobState.Skipped)]
	[InlineData(ContinuationTrigger.Failure, false, JobState.Pending)]
	[InlineData(ContinuationTrigger.Complete, true, JobState.Pending)]
	[InlineData(ContinuationTrigger.Complete, false, JobState.Pending)]
	public async Task ContinuationTriggersMatchParentOutcome(
		ContinuationTrigger trigger,
		bool parentSucceeds,
		JobState expectedState
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob(now, 1) with { Id = "parent" };
		var child = CreateJob(now, 2) with
		{
			Id = "child",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueAsync(parent, cancellationToken);
		await storage.EnqueueContinuationAsync(child, [new()
		{
			ChildJobId = child.Id,
			ParentJobId = parent.Id,
			Trigger = trigger,
		}], cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		if (parentSucceeds)
			await storage.CompleteAsync(parent.Id, "worker", cancellationToken);
		else
			await storage.FailAsync(parent.Id, "worker", "expected", nextRetryAt: null, cancellationToken);
		Assert.Equal(expectedState, (await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State);
	}

	[Theory]
	[InlineData(true, false, JobState.Skipped)]
	[InlineData(false, false, JobState.Skipped)]
	[InlineData(true, true, JobState.Pending)]
	[InlineData(false, true, JobState.Pending)]
	public async Task MixedTriggersUseEveryIncomingEdgeRegardlessOfCompletionOrder(
		bool failureParentSettlesFirst,
		bool failureParentFails,
		JobState expectedState
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var successParent = CreateJob(now, 0) with { Id = "success-parent" };
		var failureParent = CreateJob(now, 1) with { Id = "failure-parent" };
		var child = CreateJob(now, 2) with { Id = "child" };
		await storage.EnqueueAsync(successParent, cancellationToken);
		await storage.EnqueueAsync(failureParent, cancellationToken);
		await storage.EnqueueContinuationAsync(child,
		[
			new() { ChildJobId = child.Id, ParentJobId = successParent.Id, Trigger = ContinuationTrigger.Success },
			new() { ChildJobId = child.Id, ParentJobId = failureParent.Id, Trigger = ContinuationTrigger.Failure },
		], cancellationToken);
		Assert.Equal(2, (await storage.AcquireJobsAsync(
			[successParent.Id, failureParent.Id],
			"worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		)).Count);

		async Task SettleSuccessParent() => await storage.CompleteAsync(successParent.Id, "worker", cancellationToken);
		async Task SettleFailureParent()
		{
			if (failureParentFails)
				await storage.FailAsync(failureParent.Id, "worker", "expected", nextRetryAt: null, cancellationToken);
			else
				await storage.CompleteAsync(failureParent.Id, "worker", cancellationToken);
		}

		if (failureParentSettlesFirst)
		{
			await SettleFailureParent();
			Assert.Equal(JobState.AwaitingContinuation, (await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State);
			await SettleSuccessParent();
		}
		else
		{
			await SettleSuccessParent();
			Assert.Equal(JobState.AwaitingContinuation, (await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State);
			await SettleFailureParent();
		}

		Assert.Equal(expectedState, (await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State);
	}

	[Fact]
	public async Task BufferedContinuationsRejectInvalidBatchRelationshipsBeforeMutation()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 0) with { Id = "current", BatchId = "batch" };
		await storage.EnqueueBatchAsync(new()
		{
			Id = "batch",
			CreatedAt = now,
			TotalJobs = 1,
			PendingCount = 1,
			State = BatchState.Executing,
		}, [current], [], cancellationToken);
		_ = Assert.Single(await storage.AcquireJobsAsync(
			[current.Id], "worker", TimeSpan.FromMinutes(1), cancellationToken
		));

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			"worker",
			[new()
			{
				Job = CreateJob(now, 1) with { Id = "detached", BatchId = "batch" },
				Options = ContinuationOptions.Detached,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			"worker",
			[new()
			{
				Job = CreateJob(now, 2) with { Id = "wrong-batch", BatchId = "other" },
				Options = ContinuationOptions.BesideContinuations,
			}],
			cancellationToken
		).AsTask());

		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.Id, cancellationToken))!.State);
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "detached" }, cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "wrong-batch" }, cancellationToken));
	}

	[Fact]
	public async Task BufferedContinuationsRejectUnknownOptionsAndTriggersBeforeMutation()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 0) with { Id = "current" };
		await storage.EnqueueAsync(current, cancellationToken);
		_ = Assert.Single(await storage.AcquireJobsAsync(
			[current.Id], "worker", TimeSpan.FromMinutes(1), cancellationToken
		));

		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			"worker",
			[new()
			{
				Job = CreateJob(now, 1) with { Id = "unknown-option" },
				Options = (ContinuationOptions)42,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			"worker",
			[new()
			{
				Job = CreateJob(now, 2) with { Id = "unknown-trigger" },
				Options = ContinuationOptions.Detached,
				Trigger = (ContinuationTrigger)42,
			}],
			cancellationToken
		).AsTask());

		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.Id, cancellationToken))!.State);
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "unknown-option" }, cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "unknown-trigger" }, cancellationToken));
	}

	[Fact]
	public async Task AddBatchJobRejectsWrongBatchAndUnknownOptionBeforeMutation()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 0) with { Id = "current", BatchId = "batch" };
		await storage.EnqueueBatchAsync(new()
		{
			Id = "batch",
			CreatedAt = now,
			TotalJobs = 1,
			PendingCount = 1,
			State = BatchState.Executing,
		}, [current], [], cancellationToken);
		_ = Assert.Single(await storage.AcquireJobsAsync(
			[current.Id], "worker", TimeSpan.FromMinutes(1), cancellationToken
		));

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.AddBatchJobAsync(
			current.Id,
			CreateJob(now, 1) with { Id = "wrong-batch", BatchId = "other" },
			ContinuationOptions.BesideContinuations,
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.AddBatchJobAsync(
			current.Id,
			CreateJob(now, 2) with { Id = "unknown-option", BatchId = "batch" },
			(ContinuationOptions)42,
			cancellationToken
		).AsTask());

		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "wrong-batch" }, cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "unknown-option" }, cancellationToken));
	}

	[Fact]
	public async Task BatchCountersAndCancellationRemainAtomic()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob(now, 1) with { Id = "batch-parent", BatchId = "batch" };
		var child = CreateJob(now, 2) with
		{
			Id = "batch-child",
			BatchId = "batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(new()
		{
			Id = "batch",
			CreatedAt = now,
			TotalJobs = 2,
			PendingCount = 2,
			State = BatchState.Executing,
		}, [parent, child], [new() { ChildJobId = child.Id, ParentJobId = parent.Id }], cancellationToken);

		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		await storage.CompleteAsync(parent.Id, "worker", cancellationToken);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State);
		await storage.CancelBatchAsync("batch", cancellationToken);
		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(BatchState.Cancelled, batch.State);
		Assert.Equal(1, batch.Succeeded);
		Assert.Equal(1, batch.Cancelled);
		Assert.Equal(0, batch.Remaining);
	}

	private static JobAcquisitionRequest CreateRequest(string workerId, int batchSize) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = batchSize,
		Queues = [new()
		{
			QueueName = JobQueueDefinition.DefaultName,
			Capacity = batchSize,
			JobCapacities = new Dictionary<string, int> { ["storage-test"] = batchSize },
		}],
	};

	private static JobAcquisitionRequest CreateFairRequest(
		string workerId,
		int batchSize,
		FairQueuePolicy? policy = null
	) =>
		CreateRequest(workerId, batchSize) with
		{
			FairQueues = policy ?? new(0.10, 30, GroupRoundRobin: true),
		};

	private static ValueTask Enqueue(
		LinqToDBJobStorage storage,
		DateTimeOffset now,
		string id,
		int order,
		string? groupId,
		CancellationToken cancellationToken,
		string jobName = "storage-test"
	) => storage.EnqueueAsync(CreateJob(now, order) with
	{
		Id = id,
		JobName = jobName,
		GroupId = groupId,
	}, cancellationToken);

	private static async Task InsertCursorAsync(
		DataOptions options,
		string groupId,
		long sequence,
		CancellationToken cancellationToken
	)
	{
		await using var connection = new DataConnection(options);
		_ = await connection.ExecuteAsync(
			string.Create(CultureInfo.InvariantCulture, $"""
			INSERT INTO "immediate_fair_queue_groups" ("QueueName", "GroupId", "LastServedSequence", "ConcurrencyStamp")
			VALUES ('{JobQueueDefinition.DefaultName}', '{groupId}', {sequence}, '{Guid.NewGuid()}')
			"""),
			cancellationToken
		);
	}

	private static async Task<int> DeleteCursorAsync(
		DataOptions options,
		string groupId,
		CancellationToken cancellationToken
	)
	{
		await using var connection = new DataConnection(options);
		return await connection.ExecuteAsync(
			$"""
			DELETE FROM "immediate_fair_queue_groups"
			WHERE "QueueName" = '{JobQueueDefinition.DefaultName}' AND "GroupId" = '{groupId}'
			""",
			cancellationToken
		);
	}

	private static JobRecord CreateJob(DateTimeOffset now, int index) => new()
	{
		Id = "job-" + Guid.NewGuid().ToString("N"),
		JobName = "storage-test",
		Payload = string.Create(CultureInfo.InvariantCulture, $"{{\"index\":{index}}}"),
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now.AddTicks(index),
	};

	private sealed class StorageFixture(string databasePath, DataOptions options) : IAsyncDisposable
	{
		private readonly List<LinqToDBJobStorage> _storages = [];

		public DataOptions Options { get; } = options;
		public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero));

		public LinqToDBJobStorage CreateStorage()
		{
			var storage = new LinqToDBJobStorage(Options, timeProvider: TimeProvider);
			_storages.Add(storage);
			return storage;
		}

		public static async Task<StorageFixture> CreateAsync(CancellationToken cancellationToken)
		{
			var path = Path.Combine(Path.GetTempPath(), $"immediate-jobs-{Guid.NewGuid():N}.db");
			var options = new DataOptions().UseSQLite($"Data Source={path}");
			var fixture = new StorageFixture(path, options);
			try
			{
				await options.CreateImmediateJobsSchemaAsync(cancellationToken: cancellationToken);
				return fixture;
			}
			catch
			{
				await fixture.DisposeAsync();
				throw;
			}
		}

		public async ValueTask DisposeAsync()
		{
			foreach (var storage in _storages.AsEnumerable().Reverse())
				await storage.DisposeAsync();
			SqliteConnection.ClearAllPools();
			File.Delete(databasePath);
		}
	}
}
#pragma warning restore CS1591

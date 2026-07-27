using Immediate.Jobs.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class EntityFrameworkCoreJobStorageTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("{\"usage\":{\"userId\":\"42\"}}")]
	public async Task ContextRoundTripsThroughEntityFrameworkCoreStorage(string? context)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with
		{
			Context = context,
			GroupId = "context-group",
		};

		await storage.EnqueueAsync(job, cancellationToken);

		var queried = Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken));
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("context-worker", 1), cancellationToken));
		Assert.Equal(context, queried.Context);
		Assert.Equal(context, acquired.Context);
		Assert.Equal(job.GroupId, queried.GroupId);
		Assert.Equal(job.GroupId, acquired.GroupId);
	}

	[Fact]
	public async Task CompetingNodesClaimEachInvocationOnce()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();

		foreach (var index in Enumerable.Range(0, 64))
			await first.EnqueueAsync(CreateJob(now, index), cancellationToken);

		var firstClaim = first.AcquireDueJobsAsync(CreateRequest("node-a", 64), cancellationToken).AsTask();
		var secondClaim = second.AcquireDueJobsAsync(CreateRequest("node-b", 64), cancellationToken).AsTask();
		var claims = await Task.WhenAll(firstClaim, secondClaim);
		var claimed = claims.SelectMany(static claim => claim).ToArray();
		Assert.Equal(64, claimed.Length);
		Assert.Equal(64, claimed.Select(job => job.Id).Distinct().Count());
	}

	[Fact]
	public async Task CompetingFairQueueNodesClaimDistinctInvocations()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await first.EnqueueAsync(CreateJob(now, 1) with { Id = "group-a-job", GroupId = "group-a" }, cancellationToken);
		await first.EnqueueAsync(CreateJob(now, 2) with { Id = "group-b-job", GroupId = "group-b" }, cancellationToken);

		var firstClaim = first.AcquireDueJobsAsync(CreateFairRequest("node-a", 1), cancellationToken).AsTask();
		var secondClaim = second.AcquireDueJobsAsync(CreateFairRequest("node-b", 1), cancellationToken).AsTask();
		var claimed = (await Task.WhenAll(firstClaim, secondClaim)).SelectMany(static jobs => jobs).ToArray();

		Assert.Equal(2, claimed.Length);
		Assert.Equal(2, claimed.Select(static job => job.Id).Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task ExpiredLeaseIsRecoveredByAnotherNode()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1);
		await first.EnqueueAsync(job, cancellationToken);

		_ = Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));

		var recovered = Assert.Single(
			await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken)
		);
		Assert.Equal(job.Id, recovered.Id);
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-b", recovered.WorkerId);
	}

	[Fact]
	public async Task FairQueuesRotateGroupsAcrossCapacityOneAcquisitions()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var firstA = CreateJob(now, 1) with { Id = "a-first", GroupId = "group-a" };
		var secondA = CreateJob(now, 2) with { Id = "a-second", GroupId = "group-a" };
		var firstB = CreateJob(now, 3) with { Id = "b-first", GroupId = "group-b" };
		await storage.EnqueueAsync(firstA, cancellationToken);
		await storage.EnqueueAsync(secondA, cancellationToken);
		await storage.EnqueueAsync(firstB, cancellationToken);

		var request = CreateFairRequest("fair-worker", 1);
		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal(firstA.Id, first.Id);
		await storage.CompleteAsync(first.Id, "fair-worker", cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal(firstB.Id, second.Id);
	}

	[Fact]
	public async Task FairQueuesInterleaveGroupsWithinOneAcquisition()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(
			CreateJob(now, 1) with { Id = "a-first", GroupId = "group-a" },
			cancellationToken
		);
		await storage.EnqueueAsync(
			CreateJob(now, 2) with { Id = "a-second", GroupId = "group-a" },
			cancellationToken
		);
		await storage.EnqueueAsync(
			CreateJob(now, 3) with { Id = "b-first", GroupId = "group-b" },
			cancellationToken
		);
		await storage.EnqueueAsync(
			CreateJob(now, 4) with { Id = "b-second", GroupId = "group-b" },
			cancellationToken
		);

		var acquired = await storage.AcquireDueJobsAsync(
			CreateFairRequest("fair-worker", 4),
			cancellationToken
		);

		Assert.Equal(
			["a-first", "b-first", "a-second", "b-second"],
			acquired.Select(static job => job.Id)
		);
	}

	[Fact]
	public async Task NewGroupAdvancesAheadOfPreviouslyServedBacklog()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		foreach (var index in Enumerable.Range(0, 128))
		{
			await storage.EnqueueAsync(
				CreateJob(now, index) with
				{
					Id = $"a-{index:D3}",
					GroupId = "group-a",
				},
				cancellationToken
			);
		}

		var request = CreateFairRequest("fair-worker", 1);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("a-000", first.Id);
		await storage.CompleteAsync(first.Id, "fair-worker", cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 128) with { Id = "b-new", GroupId = "group-b" }, cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("b-new", second.Id);
	}

	[Fact]
	public async Task GroupIdsDoNotChangeEntityFrameworkCoreOrderingWhenFairQueuesAreDisabled()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { Id = "a-first", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { Id = "a-second", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with { Id = "b-first", GroupId = "group-b" }, cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("legacy-worker", 1), cancellationToken));
		Assert.Equal("a-first", first.Id);
		await storage.CompleteAsync(first.Id, "legacy-worker", cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("legacy-worker", 1), cancellationToken));
		Assert.Equal("a-second", second.Id);
	}

	[Fact]
	public async Task FairQueuesServeQuietGroupBeforeNoisyGroup()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { Id = "active-a-1", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { Id = "active-a-2", GroupId = "group-a" }, cancellationToken);
		Assert.Equal(2, (await storage.AcquireDueJobsAsync(CreateRequest("active-worker", 2), cancellationToken)).Count);

		await storage.EnqueueAsync(CreateJob(now, 3) with { Id = "waiting-a", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 4) with { Id = "waiting-b", GroupId = "group-b" }, cancellationToken);
		var request = CreateFairRequest(
			"fair-worker",
			1,
			new(ConcurrencyShareThreshold: 0.50, MinInflightForNoisy: 2, GroupRoundRobin: true)
		);

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("waiting-b", acquired.Id);
	}

	[Fact]
	public async Task FairQueuesKeepUngroupedJobsInExistingOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 3) with { Id = "ungrouped-third" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 1) with { Id = "ungrouped-first" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { Id = "ungrouped-second" }, cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(CreateFairRequest("fair-worker", 3), cancellationToken);

		Assert.Equal(["ungrouped-first", "ungrouped-second", "ungrouped-third"], acquired.Select(static job => job.Id));
	}

	[Fact]
	public async Task DisablingGroupRoundRobinUsesExistingDueOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { Id = "a-first", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { Id = "a-second", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with { Id = "b-first", GroupId = "group-b" }, cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(CreateFairRequest("fair-worker", 1), cancellationToken));
		Assert.Equal("a-first", first.Id);
		await storage.CompleteAsync(first.Id, "fair-worker", cancellationToken);
		var withoutRoundRobin = CreateFairRequest(
			"fair-worker",
			1,
			new(ConcurrencyShareThreshold: 1, MinInflightForNoisy: 30, GroupRoundRobin: false)
		);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(withoutRoundRobin, cancellationToken));
		Assert.Equal("a-second", second.Id);
	}

	[Fact]
	public async Task ExpiredLeasesDoNotMakeAGroupNoisy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { Id = "expired-a-1", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { Id = "expired-a-2", GroupId = "group-a" }, cancellationToken);
		Assert.Equal(2, (await storage.AcquireDueJobsAsync(CreateRequest("expired-worker", 2), cancellationToken)).Count);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
		await storage.EnqueueAsync(CreateJob(now, 3) with { Id = "waiting-b", GroupId = "group-b" }, cancellationToken);
		var request = CreateFairRequest(
			"fair-worker",
			1,
			new(ConcurrencyShareThreshold: 0.10, MinInflightForNoisy: 1, GroupRoundRobin: false)
		);

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));

		Assert.Equal("expired-a-1", acquired.Id);
	}

	[Fact]
	public async Task ReturningGroupRejoinsFairQueueWithoutHistoricalCursorDebt()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { Id = "a-first", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { Id = "a-second", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with { Id = "b-first", GroupId = "group-b" }, cancellationToken);
		var request = CreateFairRequest("fair-worker", 1);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("a-first", first.Id);
		await storage.CompleteAsync(first.Id, "fair-worker", cancellationToken);
		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("b-first", second.Id);
		await storage.CompleteAsync(second.Id, "fair-worker", cancellationToken);

		await storage.EnqueueAsync(CreateJob(now, 4) with { Id = "b-returned", GroupId = "group-b" }, cancellationToken);
		var returned = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));

		Assert.Equal("b-returned", returned.Id);
	}

	[Fact]
	public async Task FairQueuesHonorJobNameCapacity()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { GroupId = "group-b" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with { GroupId = "group-c" }, cancellationToken);
		var request = CreateFairRequest("fair-worker", 3) with
		{
			Queues =
			[
				new()
				{
					QueueName = JobQueueDefinition.DefaultName,
					Capacity = 2,
					JobCapacities = new Dictionary<string, int> { ["ef-test"] = 1 },
				},
			],
		};

		var acquired = await storage.AcquireDueJobsAsync(request, cancellationToken);

		_ = Assert.Single(acquired);
	}

	[Fact]
	public async Task SingleServerRestoresDurableEfJobsIntoMemory()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		using var firstProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1);
		await firstProcess.EnqueueAsync(job, cancellationToken);

		using var restartedProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		await restartedProcess.InitializeAsync(cancellationToken);

		Assert.Equal(job.Id, Assert.Single(await restartedProcess.QueryJobsAsync(new(), cancellationToken)).Id);
		Assert.Equal(
			job.Id,
			Assert.Single(await restartedProcess.AcquireDueJobsAsync(CreateRequest("restarted", 1), cancellationToken)).Id
		);
	}

	[Fact]
	public async Task SingleServerMirrorsFairSelectionAndGroupIds()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var now = fixture.TimeProvider.GetUtcNow();
		await using (var firstProcess = new SingleServerJobStorage(
			fixture.CreateStorage(),
			fixture.TimeProvider
		))
		{
			await firstProcess.EnqueueAsync(
				CreateJob(now, 1) with { Id = "a-first", GroupId = "group-a" },
				cancellationToken
			);
			await firstProcess.EnqueueAsync(
				CreateJob(now, 2) with { Id = "a-second", GroupId = "group-a" },
				cancellationToken
			);
			var request = CreateFairRequest("single-server", 1);
			var first = Assert.Single(await firstProcess.AcquireDueJobsAsync(request, cancellationToken));
			Assert.Equal("a-first", first.Id);
			await firstProcess.CompleteAsync(first.Id, "single-server", cancellationToken);
			await firstProcess.EnqueueAsync(
				CreateJob(now, 3) with { Id = "b-first", GroupId = "group-b" },
				cancellationToken
			);

			var second = Assert.Single(await firstProcess.AcquireDueJobsAsync(request, cancellationToken));
			Assert.Equal("b-first", second.Id);
			Assert.Equal("group-b", second.GroupId);
			var durableSecond = Assert.Single(await firstProcess.DurableStorage.QueryJobsAsync(
				new() { Id = second.Id },
				cancellationToken
			));
			Assert.Equal(JobState.Active, durableSecond.State);
			Assert.Equal("group-b", durableSecond.GroupId);
		}

		await using var restartedProcess = new SingleServerJobStorage(
			fixture.CreateStorage(),
			fixture.TimeProvider
		);
		await restartedProcess.InitializeAsync(cancellationToken);
		var restored = await restartedProcess.QueryJobsAsync(new(), cancellationToken);
		Assert.Contains(restored, static job => job.Id == "a-second" && job.GroupId == "group-a");
		Assert.Contains(restored, static job => job.Id == "b-first" && job.GroupId == "group-b");
	}

	[Fact]
	public async Task RecurringMaterializationRunsInsideConfiguredExecutionStrategy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			useRetryingExecutionStrategy: true
		);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var nextRunAt = now.AddMinutes(1);
		var schedule = new RecurringJobSchedule
		{
			Name = "retrying-strategy",
			JobName = "ef-test",
			Cron = "0 * * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		var job = CreateJob(now, 1) with
		{
			RecurringKey = $"{schedule.Name}:{schedule.NextRunAt.UtcTicks}",
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);

		var materialized = await storage.MaterializeRecurringAsync(
			schedule,
			job,
			nextRunAt,
			cancellationToken
		);

		Assert.True(materialized);
		Assert.Equal(job.Id, Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken)).Id);
		var storedSchedule = Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
		Assert.Equal(now, storedSchedule.LastRunAt);
		Assert.Equal(nextRunAt, storedSchedule.NextRunAt);
	}

	[Fact]
	public async Task CodeDefinedScheduleCannotBeReplacedByDynamicScheduleInEntityFrameworkCore()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var codeDefined = new RecurringJobSchedule
		{
			Name = "cleanup",
			JobName = "cleanup",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			IsPaused = true,
			NextRunAt = fixture.TimeProvider.GetUtcNow() + TimeSpan.FromHours(1),
		};
		await storage.UpsertRecurringAsync(codeDefined, cancellationToken);

		var exception = await Assert.ThrowsAsync<ImmediateJobException>(() =>
			storage.UpsertRecurringAsync(
				codeDefined with { Cron = "0 0 * * *", IsCodeDefined = false },
				cancellationToken
			).AsTask()
		);
		Assert.Equal("Code-defined recurring schedules cannot be replaced by dynamic schedules.", exception.Message);

		await storage.UpsertRecurringAsync(codeDefined with { Cron = "0 0 * * *", IsPaused = false }, cancellationToken);
		var stored = Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
		Assert.Equal("0 0 * * *", stored.Cron);
		Assert.True(stored.IsCodeDefined);
		Assert.True(stored.IsPaused);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ObsoleteCodeDefinedSchedulesAreRemovedFromEntityFrameworkCore(bool preserveCurrent)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var current = CreateSchedule("current", isCodeDefined: true, fixture.TimeProvider);
		var obsolete = CreateSchedule("obsolete", isCodeDefined: true, fixture.TimeProvider);
		var dynamic = CreateSchedule("dynamic", isCodeDefined: false, fixture.TimeProvider);
		await storage.UpsertRecurringAsync(current, cancellationToken);
		await storage.UpsertRecurringAsync(obsolete, cancellationToken);
		await storage.UpsertRecurringAsync(dynamic, cancellationToken);

		await storage.RemoveObsoleteCodeDefinedRecurringAsync(
			preserveCurrent ? [current.Name] : [],
			cancellationToken
		);

		var expectedNames = preserveCurrent ? ["current", "dynamic"] : new[] { "dynamic" };
		var names = (await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring
			.Select(static schedule => schedule.Name)
			.Order(StringComparer.Ordinal);
		Assert.Equal(expectedNames.Order(StringComparer.Ordinal), names);
	}

	[Fact]
	public async Task BatchCommitAndContinuationReleaseAreAtomicInEntityFrameworkCore()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob(now, 1) with { Id = "batch-parent", BatchId = "batch-one" };
		var child = CreateJob(now, 2) with
		{
			Id = "batch-child",
			BatchId = "batch-one",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(
			new()
			{
				Id = "batch-one",
				CreatedAt = now,
				TotalJobs = 2,
				PendingCount = 2,
				State = BatchState.Executing,
			},
			[parent, child],
			[new() { ChildJobId = child.Id, ParentJobId = parent.Id }],
			cancellationToken
		);

		Assert.Equal(2, (await storage.GetBatchStatusAsync("batch-one", cancellationToken))!.Total);
		var acquiredParent = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("ef-worker", 1), cancellationToken));
		Assert.Equal(parent.Id, acquiredParent.Id);
		await storage.CompleteAsync(parent.Id, "ef-worker", cancellationToken);
		Assert.Equal(
			JobState.Pending,
			(await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State
		);

		var acquiredChild = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("ef-worker", 1), cancellationToken));
		Assert.Equal(child.Id, acquiredChild.Id);
		await storage.CompleteAsync(child.Id, "ef-worker", cancellationToken);
		var status = await storage.GetBatchStatusAsync("batch-one", cancellationToken);
		Assert.Equal(BatchState.Succeeded, status!.State);
		Assert.Equal(2, status.Succeeded);
		Assert.Equal(0, status.Remaining);
	}

	[Theory]
	[InlineData(true, JobState.Pending)]
	[InlineData(false, JobState.Cancelled)]
	public async Task EntityFrameworkCoreFailureContinuationRunsOnlyWhenParentFails(
		bool parentFails,
		JobState expectedState
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob(now, 1) with { Id = "failure-trigger-parent" };
		var child = CreateJob(now, 2) with
		{
			Id = "failure-trigger-child",
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
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("failure-worker", 1), cancellationToken));

		if (parentFails)
			await storage.FailAsync(parent.Id, "failure-worker", "expected", nextRetryAt: null, cancellationToken);
		else
			await storage.CompleteAsync(parent.Id, "failure-worker", cancellationToken);

		Assert.Equal(expectedState, (await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State);
	}

	[Fact]
	public async Task InvalidEntityFrameworkCoreBatchRollsBackEveryRow()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var child = CreateJob(now, 1) with
		{
			Id = "invalid-child",
			BatchId = "invalid-batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};

		_ = await Assert.ThrowsAnyAsync<Exception>(() => storage.EnqueueBatchAsync(
			new()
			{
				Id = "invalid-batch",
				CreatedAt = now,
				TotalJobs = 1,
				PendingCount = 1,
				State = BatchState.Executing,
			},
			[child],
			[new() { ChildJobId = child.Id, ParentJobId = "missing-parent" }],
			cancellationToken
		).AsTask());

		Assert.Null(await storage.GetBatchStatusAsync("invalid-batch", cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new(), cancellationToken));
	}

	[Fact]
	public async Task PurgeRunsInsideConfiguredExecutionStrategy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			useRetryingExecutionStrategy: true
		);
		var storage = fixture.CreateStorage();

		await storage.PurgeJobsAsync(
			TimeSpan.FromHours(1),
			TimeSpan.FromHours(1),
			cancellationToken
		);
		await storage.PurgeBatchesAsync(
			TimeSpan.FromHours(1),
			TimeSpan.FromHours(1),
			cancellationToken
		);
	}

	[Fact]
	public async Task EntityFrameworkCoreCancelsAndDeletesBatchAsOneUnit()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			useRetryingExecutionStrategy: true
		);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob(now, 1) with { Id = "cancel-parent", BatchId = "cancel-batch" };
		var child = CreateJob(now, 2) with
		{
			Id = "cancel-child",
			BatchId = "cancel-batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(
			new()
			{
				Id = "cancel-batch",
				CreatedAt = now,
				TotalJobs = 2,
				PendingCount = 2,
				State = BatchState.Executing,
			},
			[parent, child],
			[new() { ChildJobId = child.Id, ParentJobId = parent.Id }],
			cancellationToken
		);

		await storage.CancelBatchAsync("cancel-batch", cancellationToken);

		var cancelled = Assert.IsType<BatchStatus>(
			await storage.GetBatchStatusAsync("cancel-batch", cancellationToken)
		);
		Assert.Equal(BatchState.Cancelled, cancelled.State);
		Assert.Equal(2, cancelled.Cancelled);
		Assert.All(
			await storage.QueryBatchMembersAsync("cancel-batch", new(), cancellationToken),
			static member => Assert.Equal(JobState.Cancelled, member.State)
		);

		await storage.DeleteBatchAsync("cancel-batch", cancellationToken);

		Assert.Null(await storage.GetBatchStatusAsync("cancel-batch", cancellationToken));
		Assert.Null(await storage.GetBatchGraphAsync("cancel-batch", cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new(), cancellationToken));
	}

	private static JobAcquisitionRequest CreateRequest(string workerId, int batchSize) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = batchSize,
		Queues = [new() { QueueName = JobQueueDefinition.DefaultName, Capacity = batchSize, JobCapacities = new Dictionary<string, int> { ["ef-test"] = batchSize } }],
	};

	private static JobAcquisitionRequest CreateFairRequest(
		string workerId,
		int batchSize,
		FairQueuePolicy? policy = null
	) => CreateRequest(workerId, batchSize) with
	{
		FairQueues = policy ?? new(
			ConcurrencyShareThreshold: 0.10,
			MinInflightForNoisy: 30,
			GroupRoundRobin: true
		),
	};

	private static JobRecord CreateJob(DateTimeOffset now, int index) => new()
	{
		Id = "job-" + Guid.NewGuid().ToString("N"),
		JobName = "ef-test",
		Payload = $"{{\"index\":{index}}}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now.AddTicks(index),
	};

	private static RecurringJobSchedule CreateSchedule(
		string name,
		bool isCodeDefined,
		TimeProvider timeProvider
	) => new()
	{
		Name = name,
		JobName = "ef-test",
		Cron = "0 * * * *",
		TimeZone = "UTC",
		IsCodeDefined = isCodeDefined,
		NextRunAt = timeProvider.GetUtcNow() + TimeSpan.FromHours(1),
	};

	private sealed class StorageFixture(
		string connectionString,
		ServiceProvider services,
		IDbContextFactory<TestDbContext> contextFactory,
		FakeTimeProvider timeProvider
	) : IAsyncDisposable
	{
		private readonly SqliteConnection _anchor = new(connectionString);

		public FakeTimeProvider TimeProvider { get; } = timeProvider;

		public EntityFrameworkCoreJobStorage<TestDbContext> CreateStorage() => new(contextFactory, TimeProvider);

		public static async Task<StorageFixture> CreateAsync(
			CancellationToken cancellationToken,
			bool useRetryingExecutionStrategy = false
		)
		{
			var connectionString = $"Data Source=jobs-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
			var services = new ServiceCollection();
			_ = services.AddDbContextFactory<TestDbContext>(options =>
			{
				_ = options.UseSqlite(connectionString);
				if (useRetryingExecutionStrategy)
					_ = options.ReplaceService<IExecutionStrategyFactory, RetryingExecutionStrategyFactory>();
			});
			var provider = services.BuildServiceProvider();
			var factory = provider.GetRequiredService<IDbContextFactory<TestDbContext>>();
			var fixture = new StorageFixture(
				connectionString,
				provider,
				factory,
				new(new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero))
			);
			try
			{
				await fixture._anchor.OpenAsync(cancellationToken);
				await using var context = await factory.CreateDbContextAsync(cancellationToken);
				_ = await context.Database.EnsureCreatedAsync(cancellationToken);
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
			await services.DisposeAsync();
			await _anchor.DisposeAsync();
		}
	}

	private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
	{
		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			_ = modelBuilder.AddImmediateJobs();
		}
	}

	private sealed class RetryingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
		: IExecutionStrategyFactory
	{
		public IExecutionStrategy Create() => new RetryingExecutionStrategy(dependencies);
	}

	private sealed class RetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
		: ExecutionStrategy(dependencies, DefaultMaxRetryCount, DefaultMaxDelay)
	{
		protected override bool ShouldRetryOn(Exception exception) => false;
	}
}
#pragma warning restore CS1591

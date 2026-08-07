using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class EntityFrameworkCoreJobStorageTests
{
	[Theory]
	[InlineData(null)]
	[InlineData(/*lang=json,strict*/ "{\"usage\":{\"userId\":\"42\"}}")]
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
		Assert.Equal(64, claimed.Select(job => job.JobId).Distinct().Count());
	}

	[Fact]
	public async Task CompetingFairQueueNodesClaimDistinctInvocations()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await first.EnqueueAsync(CreateJob(now, 1) with { JobId = "group-a-job", GroupId = "group-a" }, cancellationToken);
		await first.EnqueueAsync(CreateJob(now, 2) with { JobId = "group-b-job", GroupId = "group-b" }, cancellationToken);

		var firstClaim = first.AcquireDueJobsAsync(CreateFairRequest("node-a", 1), cancellationToken).AsTask();
		var secondClaim = second.AcquireDueJobsAsync(CreateFairRequest("node-b", 1), cancellationToken).AsTask();
		var claimed = (await Task.WhenAll(firstClaim, secondClaim)).SelectMany(static jobs => jobs).ToArray();

		Assert.Equal(2, claimed.Length);
		Assert.Equal(2, claimed.Select(static job => job.JobId).Distinct(StringComparer.Ordinal).Count());
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
		Assert.Equal(job.JobId, recovered.JobId);
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-b", recovered.WorkerId);
	}

	[Fact]
	public async Task CancellationRetriesAfterConcurrencyConflict()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var interceptor = new CancelConcurrencyInterceptor();
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			interceptor: interceptor
		);
		var storage = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with { JobId = "cancel-contention" };
		await storage.EnqueueAsync(job, cancellationToken);

		await storage.CancelAsync(job.JobId, cancellationToken);

		Assert.Equal(1, interceptor.Conflicts);
		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(job.JobId, cancellationToken))!.State);
	}

	[Fact]
	public async Task FairQueuesRotateGroupsAcrossCapacityOneAcquisitions()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var firstA = CreateJob(now, 1) with { JobId = "a-first", GroupId = "group-a" };
		var secondA = CreateJob(now, 2) with { JobId = "a-second", GroupId = "group-a" };
		var firstB = CreateJob(now, 3) with { JobId = "b-first", GroupId = "group-b" };
		await storage.EnqueueAsync(firstA, cancellationToken);
		await storage.EnqueueAsync(secondA, cancellationToken);
		await storage.EnqueueAsync(firstB, cancellationToken);

		var request = CreateFairRequest("fair-worker", 1);
		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal(firstA.JobId, first.JobId);
		await storage.CompleteAsync(first.JobId, 1, "fair-worker", cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal(firstB.JobId, second.JobId);
	}

	[Fact]
	public async Task FairQueuesInterleaveGroupsWithinOneAcquisition()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(
			CreateJob(now, 1) with { JobId = "a-first", GroupId = "group-a" },
			cancellationToken
		);
		await storage.EnqueueAsync(
			CreateJob(now, 2) with { JobId = "a-second", GroupId = "group-a" },
			cancellationToken
		);
		await storage.EnqueueAsync(
			CreateJob(now, 3) with { JobId = "b-first", GroupId = "group-b" },
			cancellationToken
		);
		await storage.EnqueueAsync(
			CreateJob(now, 4) with { JobId = "b-second", GroupId = "group-b" },
			cancellationToken
		);

		var acquired = await storage.AcquireDueJobsAsync(
			CreateFairRequest("fair-worker", 4),
			cancellationToken
		);

		Assert.Equal(
			["a-first", "b-first", "a-second", "b-second"],
			acquired.Select(static job => job.JobId)
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
					JobId = string.Create(CultureInfo.InvariantCulture, $"a-{index:D3}"),
					GroupId = "group-a",
				},
				cancellationToken
			);
		}

		var request = CreateFairRequest("fair-worker", 1);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("a-000", first.JobId);
		await storage.CompleteAsync(first.JobId, 1, "fair-worker", cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 128) with { JobId = "b-new", GroupId = "group-b" }, cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("b-new", second.JobId);
	}

	[Fact]
	public async Task GroupIdsDoNotChangeEntityFrameworkCoreOrderingWhenFairQueuesAreDisabled()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { JobId = "a-first", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { JobId = "a-second", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with { JobId = "b-first", GroupId = "group-b" }, cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("legacy-worker", 1), cancellationToken));
		Assert.Equal("a-first", first.JobId);
		await storage.CompleteAsync(first.JobId, 1, "legacy-worker", cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("legacy-worker", 1), cancellationToken));
		Assert.Equal("a-second", second.JobId);
	}

	[Fact]
	public async Task FairQueuesServeQuietGroupBeforeNoisyGroup()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { JobId = "active-a-1", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { JobId = "active-a-2", GroupId = "group-a" }, cancellationToken);
		Assert.Equal(2, (await storage.AcquireDueJobsAsync(CreateRequest("active-worker", 2), cancellationToken)).Count);

		await storage.EnqueueAsync(CreateJob(now, 3) with { JobId = "waiting-a", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 4) with { JobId = "waiting-b", GroupId = "group-b" }, cancellationToken);
		var request = CreateFairRequest(
			"fair-worker",
			1,
			new FairQueuePolicy { ConcurrencyShareThreshold = 0.50, MinInflightForNoisy = 2, GroupRoundRobin = true }
		);

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("waiting-b", acquired.JobId);
	}

	[Fact]
	public async Task FairQueuesKeepUngroupedJobsInExistingOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 3) with { JobId = "ungrouped-third" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 1) with { JobId = "ungrouped-first" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { JobId = "ungrouped-second" }, cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(CreateFairRequest("fair-worker", 3), cancellationToken);

		Assert.Equal(["ungrouped-first", "ungrouped-second", "ungrouped-third"], acquired.Select(static job => job.JobId));
	}

	[Fact]
	public async Task DisablingGroupRoundRobinUsesExistingDueOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { JobId = "a-first", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { JobId = "a-second", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with { JobId = "b-first", GroupId = "group-b" }, cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(CreateFairRequest("fair-worker", 1), cancellationToken));
		Assert.Equal("a-first", first.JobId);
		await storage.CompleteAsync(first.JobId, 1, "fair-worker", cancellationToken);
		var withoutRoundRobin = CreateFairRequest(
			"fair-worker",
			1,
			new FairQueuePolicy { ConcurrencyShareThreshold = 1, MinInflightForNoisy = 30, GroupRoundRobin = false }
		);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(withoutRoundRobin, cancellationToken));
		Assert.Equal("a-second", second.JobId);
	}

	[Fact]
	public async Task ExpiredLeasesDoNotMakeAGroupNoisy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { JobId = "expired-a-1", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { JobId = "expired-a-2", GroupId = "group-a" }, cancellationToken);
		Assert.Equal(2, (await storage.AcquireDueJobsAsync(CreateRequest("expired-worker", 2), cancellationToken)).Count);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
		await storage.EnqueueAsync(CreateJob(now, 3) with { JobId = "waiting-b", GroupId = "group-b" }, cancellationToken);
		var request = CreateFairRequest(
			"fair-worker",
			1,
			new FairQueuePolicy { ConcurrencyShareThreshold = 0.10, MinInflightForNoisy = 1, GroupRoundRobin = false }
		);

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));

		Assert.Equal("expired-a-1", acquired.JobId);
	}

	[Fact]
	public async Task ReturningGroupRejoinsFairQueueWithoutHistoricalCursorDebt()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { JobId = "a-first", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { JobId = "a-second", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with { JobId = "b-first", GroupId = "group-b" }, cancellationToken);
		var request = CreateFairRequest("fair-worker", 1);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("a-first", first.JobId);
		await storage.CompleteAsync(first.JobId, 1, "fair-worker", cancellationToken);
		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("b-first", second.JobId);
		await storage.CompleteAsync(second.JobId, 1, "fair-worker", cancellationToken);

		await storage.EnqueueAsync(CreateJob(now, 4) with { JobId = "b-returned", GroupId = "group-b" }, cancellationToken);
		var returned = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));

		Assert.Equal("b-returned", returned.JobId);
	}

	[Fact]
	public async Task GroupedEnqueueResetsCursorTransactionallyOnlyForReturningGroups()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var original = CreateJob(now, 1) with { JobId = "returning-original", GroupId = "returning-group" };
		await storage.EnqueueAsync(original, cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateFairRequest("cursor-worker", 1), cancellationToken));
		await storage.CompleteAsync(original.JobId, 1, "cursor-worker", cancellationToken);
		await fixture.InsertCursorAsync("returning-group", 42, cancellationToken);

		_ = await Assert.ThrowsAsync<DbUpdateException>(() =>
			storage.EnqueueAsync(original, cancellationToken).AsTask()
		);

		Assert.Equal(42, await fixture.GetCursorAsync("returning-group", cancellationToken));
		await storage.EnqueueAsync(
			CreateJob(now, 2) with { JobId = "returning-new", GroupId = "returning-group" },
			cancellationToken
		);
		Assert.Null(await fixture.GetCursorAsync("returning-group", cancellationToken));

		await fixture.InsertCursorAsync("live-group", 73, cancellationToken);
		await storage.EnqueueAsync(
			CreateJob(now, 3) with { JobId = "live-first", GroupId = "live-group" },
			cancellationToken
		);
		await fixture.InsertCursorAsync("live-group", 73, cancellationToken, replace: true);
		await storage.EnqueueAsync(
			CreateJob(now, 4) with { JobId = "live-second", GroupId = "live-group" },
			cancellationToken
		);
		Assert.Equal(73, await fixture.GetCursorAsync("live-group", cancellationToken));
	}

	[Fact]
	public async Task VanishedFairQueueCandidateStateDoesNotThrow()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var interceptor = new FairQueueRaceInterceptor("vanished", deleteCandidateDuringStateRead: true);
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken, interceptor: interceptor);
		var storage = fixture.CreateStorage();
		await storage.EnqueueAsync(
			CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with { JobId = "vanished", GroupId = "race-group" },
			cancellationToken
		);

		var acquired = await storage.AcquireDueJobsAsync(CreateFairRequest("race-worker", 1), cancellationToken);

		Assert.Empty(acquired);
		Assert.True(interceptor.CandidateDeleted);
	}

	[Fact]
	public async Task FairQueueStopsAfterBoundedConsecutiveClaimLosses()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var interceptor = new FairQueueRaceInterceptor("pending", sabotageCursorClaims: true);
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken, interceptor: interceptor);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { JobId = "active", GroupId = "loss-group" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { JobId = "pending", GroupId = "loss-group" }, cancellationToken);
		interceptor.Enabled = false;
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateFairRequest("seed-worker", 1), cancellationToken));
		interceptor.Enabled = true;

		var acquisition = storage.AcquireDueJobsAsync(CreateFairRequest("loss-worker", 1), cancellationToken).AsTask();
		var acquired = await acquisition.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

		Assert.Empty(acquired);
		Assert.InRange(interceptor.SabotagedClaims, 1, 10);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync("pending", cancellationToken))!.State);
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
		await using var firstProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1);
		await firstProcess.EnqueueAsync(job, cancellationToken);

		await using var restartedProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		await restartedProcess.InitializeAsync(cancellationToken);

		Assert.Equal(job.JobId, Assert.Single(await restartedProcess.QueryJobsAsync(new(), cancellationToken)).JobId);
		Assert.Equal(
			job.JobId,
			Assert.Single(await restartedProcess.AcquireDueJobsAsync(CreateRequest("restarted", 1), cancellationToken)).JobId
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
				CreateJob(now, 1) with { JobId = "a-first", GroupId = "group-a" },
				cancellationToken
			);
			await firstProcess.EnqueueAsync(
				CreateJob(now, 2) with { JobId = "a-second", GroupId = "group-a" },
				cancellationToken
			);
			var request = CreateFairRequest("single-server", 1);
			var first = Assert.Single(await firstProcess.AcquireDueJobsAsync(request, cancellationToken));
			Assert.Equal("a-first", first.JobId);
			await firstProcess.CompleteAsync(first.JobId, 1, "single-server", cancellationToken);
			await firstProcess.EnqueueAsync(
				CreateJob(now, 3) with { JobId = "b-first", GroupId = "group-b" },
				cancellationToken
			);

			var second = Assert.Single(await firstProcess.AcquireDueJobsAsync(request, cancellationToken));
			Assert.Equal("b-first", second.JobId);
			Assert.Equal("group-b", second.GroupId);
			var durableSecond = Assert.Single(await firstProcess.DurableStorage.QueryJobsAsync(
				new() { Id = second.JobId },
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
		Assert.Contains(restored, static job => string.Equals(job.JobId, "a-second", StringComparison.Ordinal) && string.Equals(job.GroupId, "group-a", StringComparison.Ordinal));
		Assert.Contains(restored, static job => string.Equals(job.JobId, "b-first", StringComparison.Ordinal) && string.Equals(job.GroupId, "group-b", StringComparison.Ordinal));
	}

	[Fact]
	public async Task FairQueueAcquisitionRunsInsideConfiguredExecutionStrategy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			useRetryingExecutionStrategy: true
		);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { JobId = "a-first", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { JobId = "a-second", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with { JobId = "b-first", GroupId = "group-b" }, cancellationToken);
		var request = CreateFairRequest("fair-worker", 1);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("a-first", first.JobId);
		await storage.CompleteAsync(first.JobId, 1, "fair-worker", cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("b-first", second.JobId);
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
			RecurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{schedule.NextRunAt.UtcTicks}"),
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);

		var materialized = await storage.MaterializeRecurringAsync(
			schedule,
			job,
			nextRunAt,
			cancellationToken
		);

		Assert.True(materialized);
		Assert.Equal(job.JobId, Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken)).JobId);
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
	public async Task DashboardMutationsDistinguishMissingAndInvalidEntityFrameworkCoreTargets()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var pending = CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with { JobId = "dashboard-pending" };
		await storage.EnqueueAsync(pending, cancellationToken);
		var codeDefined = CreateSchedule("dashboard-code", isCodeDefined: true, fixture.TimeProvider);
		var dynamic = CreateSchedule("dashboard-dynamic", isCodeDefined: false, fixture.TimeProvider);
		await storage.UpsertRecurringAsync(codeDefined, cancellationToken);
		await storage.UpsertRecurringAsync(dynamic, cancellationToken);

		_ = await Assert.ThrowsAsync<KeyNotFoundException>(() => storage.RetryAsync("missing-job", cancellationToken).AsTask());
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(() => storage.DeleteAsync("missing-job", cancellationToken).AsTask());
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(() => storage.RemoveRecurringAsync("missing-schedule", cancellationToken).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.RetryAsync(pending.JobId, cancellationToken).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.DeleteAsync(pending.JobId, cancellationToken).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.RemoveRecurringAsync(codeDefined.Name, cancellationToken).AsTask());

		await storage.RemoveRecurringAsync(dynamic.Name, cancellationToken);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(() => storage.RemoveRecurringAsync(dynamic.Name, cancellationToken).AsTask());
	}

	[Fact]
	public async Task BatchCommitAndContinuationReleaseAreAtomicInEntityFrameworkCore()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob(now, 1) with { JobId = "batch-parent", BatchId = "batch-one" };
		var child = CreateJob(now, 2) with
		{
			JobId = "batch-child",
			BatchId = "batch-one",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(
			new()
			{
				BatchId = "batch-one",
				CreatedAt = now,
				TotalJobs = 2,
				PendingCount = 2,
				State = BatchState.Executing,
			},
			[parent, child],
			[new() { ChildJobId = child.JobId, ParentJobId = parent.JobId }],
			cancellationToken
		);

		Assert.Equal(2, (await storage.GetBatchStatusAsync("batch-one", cancellationToken))!.Total);
		var acquiredParent = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("ef-worker", 1), cancellationToken));
		Assert.Equal(parent.JobId, acquiredParent.JobId);
		await storage.CompleteAsync(parent.JobId, 1, "ef-worker", cancellationToken);
		Assert.Equal(
			JobState.Pending,
			(await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State
		);

		var acquiredChild = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("ef-worker", 1), cancellationToken));
		Assert.Equal(child.JobId, acquiredChild.JobId);
		await storage.CompleteAsync(child.JobId, 1, "ef-worker", cancellationToken);
		var status = await storage.GetBatchStatusAsync("batch-one", cancellationToken);
		Assert.Equal(BatchState.Succeeded, status!.State);
		Assert.Equal(2, status.Succeeded);
		Assert.Equal(0, status.Remaining);
	}

	[Theory]
	[InlineData(true, JobState.Pending)]
	[InlineData(false, JobState.Skipped)]
	public async Task EntityFrameworkCoreFailureContinuationRunsOnlyWhenParentFails(
		bool parentFails,
		JobState expectedState
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob(now, 1) with { JobId = "failure-trigger-parent" };
		var child = CreateJob(now, 2) with
		{
			JobId = "failure-trigger-child",
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
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("failure-worker", 1), cancellationToken));

		if (parentFails)
			await storage.FailAsync(parent.JobId, 1, "failure-worker", "expected", nextRetryAt: null, cancellationToken);
		else
			await storage.CompleteAsync(parent.JobId, 1, "failure-worker", cancellationToken);

		Assert.Equal(expectedState, (await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State);
	}

	[Fact]
	public async Task IncomingEdgeLookupPreservesFieldsAndCollectionSemantics()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var jobParent = CreateJob(now, 1) with { JobId = "incoming-parent" };
		var batchParent = CreateJob(now, 2) with { JobId = "incoming-batch-parent", BatchId = "incoming-batch" };
		var child = CreateJob(now, 3) with { JobId = "incoming-child" };
		await storage.EnqueueAsync(jobParent, cancellationToken);
		await storage.EnqueueBatchAsync(
			new()
			{
				BatchId = "incoming-batch",
				CreatedAt = now,
				TotalJobs = 1,
				PendingCount = 1,
				State = BatchState.Executing,
			},
			[batchParent],
			[],
			cancellationToken
		);
		await storage.EnqueueContinuationAsync(
			child,
			[
				new() { ChildJobId = child.JobId, ParentJobId = jobParent.JobId, Trigger = ContinuationTrigger.Failure },
				new() { ChildJobId = child.JobId, ParentBatchId = "incoming-batch", Trigger = ContinuationTrigger.Complete },
			],
			cancellationToken
		);

		var edges = await storage.GetIncomingEdgesAsync(
			[child.JobId, child.JobId, "missing-child"],
			cancellationToken
		);

		Assert.Collection(
			edges,
			edge =>
			{
				Assert.Equal(child.JobId, edge.ChildJobId);
				Assert.Equal(jobParent.JobId, edge.ParentJobId);
				Assert.Null(edge.ParentBatchId);
				Assert.Equal(ContinuationTrigger.Failure, edge.Trigger);
			},
			edge =>
			{
				Assert.Equal(child.JobId, edge.ChildJobId);
				Assert.Null(edge.ParentJobId);
				Assert.Equal("incoming-batch", edge.ParentBatchId);
				Assert.Equal(ContinuationTrigger.Complete, edge.Trigger);
			}
		);
		Assert.Empty(await storage.GetIncomingEdgesAsync([], cancellationToken));
		_ = await Assert.ThrowsAsync<ArgumentException>(() =>
			storage.GetIncomingEdgesAsync([" "], cancellationToken).AsTask()
		);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MixedSuccessAndFailureTriggersCancelInEitherCompletionOrder(bool failureEdgeSettlesLast)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var successParent = CreateJob(now, 1) with { JobId = "mixed-success-parent" };
		var failureParent = CreateJob(now, 2) with { JobId = "mixed-failure-parent" };
		var child = CreateJob(now, 3) with { JobId = "mixed-child" };
		await storage.EnqueueAsync(successParent, cancellationToken);
		await storage.EnqueueAsync(failureParent, cancellationToken);
		await storage.EnqueueContinuationAsync(
			child,
			[
				new() { ChildJobId = child.JobId, ParentJobId = successParent.JobId, Trigger = ContinuationTrigger.Success },
				new() { ChildJobId = child.JobId, ParentJobId = failureParent.JobId, Trigger = ContinuationTrigger.Failure },
			],
			cancellationToken
		);
		Assert.Equal(2, (await storage.AcquireDueJobsAsync(CreateRequest("mixed-worker", 2), cancellationToken)).Count);

		var first = failureEdgeSettlesLast ? successParent.JobId : failureParent.JobId;
		var second = failureEdgeSettlesLast ? failureParent.JobId : successParent.JobId;
		await storage.CompleteAsync(first, 1, "mixed-worker", cancellationToken);
		Assert.Equal(JobState.AwaitingContinuation, (await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State);
		await storage.CompleteAsync(second, 1, "mixed-worker", cancellationToken);

		Assert.Equal(JobState.Skipped, (await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State);
	}

	[Theory]
	[InlineData(ContinuationOptions.Detached, "unexpected-batch")]
	[InlineData(ContinuationOptions.BesideContinuations, "wrong-batch")]
	public async Task CompletionRejectsInvalidContinuationBatchIdsBeforeMutation(
		ContinuationOptions options,
		string invalidBatchId
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 1) with { JobId = "validation-current", BatchId = "validation-batch" };
		await storage.EnqueueBatchAsync(
			new()
			{
				BatchId = "validation-batch",
				CreatedAt = now,
				TotalJobs = 1,
				PendingCount = 1,
				State = BatchState.Executing,
			},
			[current],
			[],
			cancellationToken
		);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("validation-worker", 1), cancellationToken));

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.JobId,
			1, "validation-worker",
			[new()
			{
				Job = CreateJob(now, 2) with { JobId = "validation-child", BatchId = invalidBatchId },
				Options = options,
			}],
			cancellationToken
		).AsTask());

		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.JobId, cancellationToken))!.State);
		Assert.Null(await storage.GetJobStatusAsync("validation-child", cancellationToken));
		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("validation-batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.AddBatchJobAsync(
			current.JobId,
			1,
			CreateJob(now, 3) with { JobId = "validation-added", BatchId = "wrong-batch" },
			ContinuationOptions.BesideContinuations,
			cancellationToken
		).AsTask());
		Assert.Null(await storage.GetJobStatusAsync("validation-added", cancellationToken));
		batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("validation-batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
	}

	[Fact]
	public async Task CompletionRejectsUnknownContinuationOptionsAndTriggersBeforeMutation()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 1) with { JobId = "enum-current" };
		await storage.EnqueueAsync(current, cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("enum-worker", 1), cancellationToken));

		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.CompleteWithContinuationsAsync(
			current.JobId,
			1, "enum-worker",
			[new()
			{
				Job = CreateJob(now, 2) with { JobId = "unknown-trigger" },
				Options = ContinuationOptions.Detached,
				Trigger = (ContinuationTrigger)int.MaxValue,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.CompleteWithContinuationsAsync(
			current.JobId,
			1, "enum-worker",
			[new()
			{
				Job = CreateJob(now, 3) with { JobId = "unknown-option" },
				Options = (ContinuationOptions)int.MaxValue,
			}],
			cancellationToken
		).AsTask());

		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.JobId, cancellationToken))!.State);
		Assert.Null(await storage.GetJobStatusAsync("unknown-trigger", cancellationToken));
		Assert.Null(await storage.GetJobStatusAsync("unknown-option", cancellationToken));
	}

	[Fact]
	public async Task EmptyBatchProjectionIsFullySettledAndStoredRetryLimitIsUnknown()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await fixture.InsertEmptyBatchAsync("empty-batch", now, cancellationToken);
		var job = CreateJob(now, 1) with { JobId = "unknown-retry-limit" };
		await storage.EnqueueAsync(job, cancellationToken);

		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("empty-batch", cancellationToken));
		Assert.Equal(1d, batch.FractionSettled);
		Assert.Null((await storage.GetJobStatusAsync(job.JobId, cancellationToken))!.MaxAttempts);
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
			JobId = "invalid-child",
			BatchId = "invalid-batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};

		_ = await Assert.ThrowsAnyAsync<Exception>(() => storage.EnqueueBatchAsync(
			new()
			{
				BatchId = "invalid-batch",
				CreatedAt = now,
				TotalJobs = 1,
				PendingCount = 1,
				State = BatchState.Executing,
			},
			[child],
			[new() { ChildJobId = child.JobId, ParentJobId = "missing-parent" }],
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
		var parent = CreateJob(now, 1) with { JobId = "cancel-parent", BatchId = "cancel-batch" };
		var child = CreateJob(now, 2) with
		{
			JobId = "cancel-child",
			BatchId = "cancel-batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(
			new()
			{
				BatchId = "cancel-batch",
				CreatedAt = now,
				TotalJobs = 2,
				PendingCount = 2,
				State = BatchState.Executing,
			},
			[parent, child],
			[new() { ChildJobId = child.JobId, ParentJobId = parent.JobId }],
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
		FairQueues = policy ?? new FairQueuePolicy
		{
			ConcurrencyShareThreshold = 0.10,
			MinInflightForNoisy = 30,
			GroupRoundRobin = true,
		},
	};

	private static JobRecord CreateJob(DateTimeOffset now, int index) => new()
	{
		JobId = "job-" + Guid.NewGuid().ToString("N"),
		JobName = "ef-test",
		Payload = string.Create(CultureInfo.InvariantCulture, $"{{\"index\":{index}}}"),
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

		public async Task InsertCursorAsync(
			string groupId,
			long sequence,
			CancellationToken cancellationToken,
			bool replace = false
		)
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			var format = replace
				? "INSERT OR REPLACE INTO immediate_fair_queue_groups (QueueName, GroupId, LastServedSequence, ConcurrencyStamp) VALUES ({0}, {1}, {2}, {3})"
				: "INSERT INTO immediate_fair_queue_groups (QueueName, GroupId, LastServedSequence, ConcurrencyStamp) VALUES ({0}, {1}, {2}, {3})";
			var command = FormattableStringFactory.Create(
				format,
				JobQueueDefinition.DefaultName,
				groupId,
				sequence,
				Guid.NewGuid()
			);
			_ = await context.Database.ExecuteSqlAsync(command, cancellationToken);
		}

		public async Task<long?> GetCursorAsync(string groupId, CancellationToken cancellationToken)
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			await context.Database.OpenConnectionAsync(cancellationToken);
			await using var command = context.Database.GetDbConnection().CreateCommand();
			command.CommandText = "SELECT LastServedSequence FROM immediate_fair_queue_groups WHERE QueueName = $queue AND GroupId = $group";
			var queueParameter = command.CreateParameter();
			queueParameter.ParameterName = "$queue";
			queueParameter.Value = JobQueueDefinition.DefaultName;
			_ = command.Parameters.Add(queueParameter);
			var groupParameter = command.CreateParameter();
			groupParameter.ParameterName = "$group";
			groupParameter.Value = groupId;
			_ = command.Parameters.Add(groupParameter);
			var value = await command.ExecuteScalarAsync(cancellationToken);
			return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
		}

		public async Task InsertEmptyBatchAsync(
			string batchId,
			DateTimeOffset createdAt,
			CancellationToken cancellationToken
		)
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			_ = await context.Database.ExecuteSqlAsync(
				$"""INSERT INTO immediate_job_batches (Id, CreatedAt, TotalJobs, PendingCount, SucceededCount, FailedCount, CancelledCount, SkippedCount, StartedAt, CompletedAt, State, ConcurrencyStamp) VALUES ({batchId}, {createdAt.UtcTicks}, 0, 0, 0, 0, 0, 0, NULL, {createdAt.UtcTicks}, {(short)BatchState.Succeeded}, {Guid.NewGuid()})""",
				cancellationToken
			);
		}

		public static async Task<StorageFixture> CreateAsync(
			CancellationToken cancellationToken,
			bool useRetryingExecutionStrategy = false,
			IInterceptor? interceptor = null
		)
		{
			var connectionString = $"Data Source=jobs-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
			if (interceptor is FairQueueRaceInterceptor raceInterceptor)
				raceInterceptor.ConnectionString = connectionString;
			var services = new ServiceCollection();
			_ = services.AddDbContextFactory<TestDbContext>(options =>
			{
				_ = options.UseSqlite(connectionString);
				if (interceptor is not null)
					_ = options.AddInterceptors(interceptor);
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

	private sealed class CancelConcurrencyInterceptor : SaveChangesInterceptor
	{
		private int _conflicts;

		public int Conflicts => Volatile.Read(ref _conflicts);

		public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
			DbContextEventData eventData,
			InterceptionResult<int> result,
			CancellationToken cancellationToken = default
		)
		{
			var cancelling = eventData.Context?.ChangeTracker
				.Entries()
				.Any(static entry =>
					entry.State == EntityState.Modified
					&& entry.Properties.Any(static property =>
						property.IsModified
						&& string.Equals(property.Metadata.Name, "State", StringComparison.Ordinal)
						&& Equals(property.CurrentValue, JobState.Cancelled))) == true;
			if (cancelling && Interlocked.CompareExchange(ref _conflicts, 1, 0) == 0)
				throw new DbUpdateConcurrencyException("Injected cancellation concurrency conflict.");

			return base.SavingChangesAsync(eventData, result, cancellationToken);
		}
	}

#pragma warning disable IDE0290 // An explicit constructor keeps captured test configuration unambiguous.
	private sealed class FairQueueRaceInterceptor : DbCommandInterceptor
	{
		private readonly string _candidateId;
		private readonly bool _deleteCandidateDuringStateRead;
		private readonly bool _sabotageCursorClaims;
		private int _candidateDeleted;
		private int _sabotagedClaims;

		public FairQueueRaceInterceptor(
			string candidateId,
			bool deleteCandidateDuringStateRead = false,
			bool sabotageCursorClaims = false
		)
		{
			_candidateId = candidateId;
			_deleteCandidateDuringStateRead = deleteCandidateDuringStateRead;
			_sabotageCursorClaims = sabotageCursorClaims;
			Enabled = deleteCandidateDuringStateRead;
		}

		public string ConnectionString { get; set; } = null!;
		public bool Enabled { get; set; }
		public bool CandidateDeleted => Volatile.Read(ref _candidateDeleted) != 0;
		public int SabotagedClaims => Volatile.Read(ref _sabotagedClaims);

		public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
			DbCommand command,
			CommandEventData eventData,
			InterceptionResult<DbDataReader> result,
			CancellationToken cancellationToken = default
		)
		{
			if (!Enabled)
				return result;

			var sql = command.CommandText;
			if (_deleteCandidateDuringStateRead
				&& Volatile.Read(ref _candidateDeleted) == 0
				&& sql.Contains("immediate_jobs", StringComparison.Ordinal)
				&& sql.Contains("immediate_fair_queue_groups", StringComparison.Ordinal))
			{
				await ExecuteMutationAsync(
					"DELETE FROM immediate_jobs WHERE Id = $id",
					("$id", _candidateId),
					cancellationToken
				);
				_ = Interlocked.Exchange(ref _candidateDeleted, 1);
			}
			else if (_sabotageCursorClaims
				&& sql.Contains("immediate_fair_queue_groups", StringComparison.Ordinal)
				&& !sql.Contains("immediate_jobs", StringComparison.Ordinal)
				&& !sql.Contains("MAX(", StringComparison.OrdinalIgnoreCase))
			{
#pragma warning disable CA2100 // Rewrites EF-generated SQL with a fixed arithmetic expression.
				command.CommandText = command.CommandText.Replace(
					"\"LastServedSequence\"",
					"\"LastServedSequence\" + 100",
					StringComparison.Ordinal
				);
#pragma warning restore CA2100
				_ = Interlocked.Increment(ref _sabotagedClaims);
			}

			return result;
		}

		private async Task ExecuteMutationAsync(
			string commandText,
			(string Name, object Value) parameter,
			CancellationToken cancellationToken
		) => await ExecuteMutationAsync(commandText, [parameter], cancellationToken);

		private async Task ExecuteMutationAsync(
			string commandText,
			IReadOnlyList<(string Name, object Value)> parameters,
			CancellationToken cancellationToken
		)
		{
			await using var connection = new SqliteConnection(ConnectionString);
			await connection.OpenAsync(cancellationToken);
			await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-only helper receives one of the fixed statements above.
			command.CommandText = commandText;
#pragma warning restore CA2100
			foreach (var (name, value) in parameters)
				_ = command.Parameters.AddWithValue(name, value);
			_ = await command.ExecuteNonQueryAsync(cancellationToken);
		}
	}
#pragma warning restore IDE0290

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

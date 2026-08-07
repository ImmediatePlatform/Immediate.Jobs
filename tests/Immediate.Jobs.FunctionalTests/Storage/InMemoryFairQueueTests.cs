using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class InMemoryFairQueueTests
{
	private static readonly FairQueuePolicy DefaultPolicy = new() { ConcurrencyShareThreshold = 0.10, MinInflightForNoisy = 30, GroupRoundRobin = true };

	[Fact]
	public async Task CapacityOneRotatesAcrossGroupsBetweenAcquisitions()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await Enqueue(storage, clock, "group-1-first", 0, "group-1", cancellationToken);
		await Enqueue(storage, clock, "group-1-second", 1, "group-1", cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-1", capacity: 1, DefaultPolicy),
			cancellationToken
		));
		await storage.CompleteAsync(first.JobId, 1, "worker-1", cancellationToken);
		await Enqueue(storage, clock, "group-2-first", 2, "group-2", cancellationToken);
		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-2", capacity: 1, DefaultPolicy),
			cancellationToken
		));

		Assert.Equal("group-1-first", first.JobId);
		Assert.Equal("group-2-first", second.JobId);
	}

	[Fact]
	public async Task DisabledFairQueuesRetainExistingDueOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await Enqueue(storage, clock, "group-1-first", 0, "group-1", cancellationToken);
		await Enqueue(storage, clock, "group-1-second", 1, "group-1", cancellationToken);
		await Enqueue(storage, clock, "group-2-first", 2, "group-2", cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-1", capacity: 1, policy: null),
			cancellationToken
		));
		await storage.CompleteAsync(first.JobId, 1, "worker-1", cancellationToken);
		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-2", capacity: 1, policy: null),
			cancellationToken
		));

		Assert.Equal("group-1-first", first.JobId);
		Assert.Equal("group-1-second", second.JobId);
	}

	[Fact]
	public async Task OneAcquisitionInterleavesGroups()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await Enqueue(storage, clock, "group-1-first", 0, "group-1", cancellationToken);
		await Enqueue(storage, clock, "group-1-second", 1, "group-1", cancellationToken);
		await Enqueue(storage, clock, "group-2-first", 2, "group-2", cancellationToken);
		await Enqueue(storage, clock, "group-2-second", 3, "group-2", cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(
			CreateRequest("worker", capacity: 4, DefaultPolicy),
			cancellationToken
		);

		Assert.Equal(
			["group-1-first", "group-2-first", "group-1-second", "group-2-second"],
			acquired.Select(static job => job.JobId)
		);
	}

	[Fact]
	public async Task NoisyGroupIsServedAfterQuietGroup()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await Enqueue(storage, clock, "noisy-active-1", 0, "noisy", cancellationToken);
		await Enqueue(storage, clock, "noisy-active-2", 1, "noisy", cancellationToken);
		_ = await storage.AcquireJobsAsync(
			["noisy-active-1", "noisy-active-2"],
			"existing-worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		);
		await Enqueue(storage, clock, "noisy-waiting", 2, "noisy", cancellationToken);
		await Enqueue(storage, clock, "quiet-waiting", 3, "quiet", cancellationToken);

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker", capacity: 1, new FairQueuePolicy { ConcurrencyShareThreshold = 0.50, MinInflightForNoisy = 2, GroupRoundRobin = true }),
			cancellationToken
		));

		Assert.Equal("quiet-waiting", acquired.JobId);
	}

	[Fact]
	public async Task ExpiredLeasesDoNotMakeAGroupNoisy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await Enqueue(storage, clock, "expired-1", 0, "noisy", cancellationToken, "ignored");
		await Enqueue(storage, clock, "expired-2", 1, "noisy", cancellationToken, "ignored");
		_ = await storage.AcquireJobsAsync(
			["expired-1", "expired-2"],
			"expired-worker",
			TimeSpan.FromSeconds(1),
			cancellationToken
		);
		await Enqueue(storage, clock, "formerly-noisy", 2, "noisy", cancellationToken);
		await Enqueue(storage, clock, "quiet", 3, "quiet", cancellationToken);
		clock.Advance(TimeSpan.FromSeconds(2));

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker", capacity: 1, new FairQueuePolicy { ConcurrencyShareThreshold = 0.50, MinInflightForNoisy = 2, GroupRoundRobin = true }),
			cancellationToken
		));

		Assert.Equal("formerly-noisy", acquired.JobId);
	}

	[Fact]
	public async Task DisablingRoundRobinUsesDueOrderAfterNoisyClassification()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await Enqueue(storage, clock, "group-1-first", 0, "group-1", cancellationToken);
		await Enqueue(storage, clock, "group-1-second", 1, "group-1", cancellationToken);
		await Enqueue(storage, clock, "group-2-first", 2, "group-2", cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-1", capacity: 1, DefaultPolicy),
			cancellationToken
		));
		await storage.CompleteAsync(first.JobId, 1, "worker-1", cancellationToken);
		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-2", capacity: 1, new FairQueuePolicy { ConcurrencyShareThreshold = 0.10, MinInflightForNoisy = 30, GroupRoundRobin = false }),
			cancellationToken
		));

		Assert.Equal("group-1-second", second.JobId);
	}

	[Fact]
	public async Task FairAcquisitionPreservesUngroupedOrderAndCapacities()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await Enqueue(storage, clock, "ungrouped-limited", 0, groupId: null, cancellationToken, "limited");
		await Enqueue(storage, clock, "grouped-limited", 1, "group-1", cancellationToken, "limited");
		await Enqueue(storage, clock, "grouped-other-1", 2, "group-2", cancellationToken, "other");
		await Enqueue(storage, clock, "grouped-other-2", 3, "group-3", cancellationToken, "other");

		var acquired = await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "worker",
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = 4,
			FairQueues = DefaultPolicy,
			Queues =
			[
				new()
				{
					QueueName = JobQueueDefinition.DefaultName,
					Capacity = 2,
					JobCapacities = new Dictionary<string, int> { ["limited"] = 1, ["other"] = 10 },
				},
			],
		}, cancellationToken);

		Assert.Equal(["ungrouped-limited", "grouped-other-1"], acquired.Select(static job => job.JobId));
	}

	[Fact]
	public async Task EnabledFairQueuesWithoutGroupedJobsUseExistingOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await Enqueue(storage, clock, "newer", 2, groupId: null, cancellationToken);
		await Enqueue(storage, clock, "oldest", 0, groupId: null, cancellationToken);
		await Enqueue(storage, clock, "middle", 1, groupId: null, cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(
			CreateRequest("worker", capacity: 3, DefaultPolicy),
			cancellationToken
		);

		Assert.Equal(["oldest", "middle", "newer"], acquired.Select(static job => job.JobId));
	}

	[Fact]
	public async Task CursorResetsAfterGroupBacklogClears()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await Enqueue(storage, clock, "group-1-first", 0, "group-1", cancellationToken);
		await Enqueue(storage, clock, "group-1-second", 1, "group-1", cancellationToken);
		await Enqueue(storage, clock, "group-2-active", 2, "group-2", cancellationToken);
		await Enqueue(storage, clock, "group-2-waiting", 3, "group-2", cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-1", capacity: 1, DefaultPolicy),
			cancellationToken
		));
		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-2", capacity: 1, DefaultPolicy),
			cancellationToken
		));
		await storage.CompleteAsync(first.JobId, 1, "worker-1", cancellationToken);
		var third = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-3", capacity: 1, DefaultPolicy),
			cancellationToken
		));
		await storage.CompleteAsync(third.JobId, 1, "worker-3", cancellationToken);
		await Enqueue(storage, clock, "group-1-returned", 4, "group-1", cancellationToken);

		var afterReset = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("worker-4", capacity: 1, DefaultPolicy),
			cancellationToken
		));

		Assert.Equal("group-1-first", first.JobId);
		Assert.Equal("group-2-active", second.JobId);
		Assert.Equal("group-1-second", third.JobId);
		Assert.Equal("group-1-returned", afterReset.JobId);
	}

	private static JobAcquisitionRequest CreateRequest(
		string workerId,
		int capacity,
		FairQueuePolicy? policy
	) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = capacity,
		FairQueues = policy,
		Queues =
		[
			new()
			{
				QueueName = JobQueueDefinition.DefaultName,
				Capacity = capacity,
				JobCapacities = new Dictionary<string, int> { ["job"] = capacity },
			},
		],
	};

	private static ValueTask Enqueue(
		InMemoryJobStorage storage,
		FakeTimeProvider clock,
		string id,
		int order,
		string? groupId,
		CancellationToken cancellationToken,
		string jobName = "job"
	) => storage.EnqueueAsync(new()
	{
		JobId = id,
		JobName = jobName,
		Payload = "{}",
		GroupId = groupId,
		State = JobState.Pending,
		DueAt = clock.GetUtcNow(),
		CreatedAt = clock.GetUtcNow().AddTicks(order),
	}, cancellationToken);
}
#pragma warning restore CS1591

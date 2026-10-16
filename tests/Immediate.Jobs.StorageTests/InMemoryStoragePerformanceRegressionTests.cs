using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryStoragePerformanceRegressionTests
{
	[Theory]
	[InlineData(1, 0, 0, 10)]
	[InlineData(0, 1, 0, 10)]
	[InlineData(0, 0, 1, 10)]
	[InlineData(0, 0, 0, 1)]
	public async Task FairAcquisitionAdvancesPastExhaustedDefinitionsWithinEachGroup(
		int maxConcurrency, int slidingMax, int fixedMax, int capacity
	)
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions =
			[
				new()
				{
					Name = "limited",
					MaxConcurrency = maxConcurrency,
					SlidingWindowMax = slidingMax,
					SlidingWindowPeriod = slidingMax > 0 ? TimeSpan.FromMinutes(1) : null,
					FixedWindowMax = fixedMax,
					FixedWindowPeriod = fixedMax > 0 ? TimeSpan.FromMinutes(1) : null,
				},
				new() { Name = "free" },
			],
		}, token);
		(string Id, string Name, string? Group)[] jobs =
		[
			("limited-a", "limited", "a"),
			("limited-a-next", "limited", "a"),
			("free-a", "free", "a"),
			("limited-b", "limited", "b"),
			("free-b", "free", "b"),
			("limited-ungrouped", "limited", null),
			("free-ungrouped", "free", null),
		];
		for (var index = 0; index < jobs.Length; index++)
		{
			var (id, name, group) = jobs[index];
			await storage.EnqueueAsync(Job(id, name, group, clock.GetUtcNow().AddTicks(index)), token);
		}

		var acquired = await storage.AcquireDueJobsAsync(Request(capacity, roundRobin: true), token);

		Assert.Equal(["limited-a", "free-b", "free-ungrouped", "free-a"], acquired.Select(static job => job.JobHandle.Value));
	}

	[Fact]
	public async Task FairAcquisitionReclassifiesNoisyGroupsAfterEachClaim()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [new() { Name = "free" }] }, token);
		for (var index = 0; index < 6; index++)
		{
			var group = index < 3 ? "a" : "b";
			var id = string.Create(CultureInfo.InvariantCulture, $"{group}-{index % 3}");
			await storage.EnqueueAsync(Job(id, "free", group, clock.GetUtcNow().AddTicks(index)), token);
		}

		var acquired = await storage.AcquireDueJobsAsync(Request(10, roundRobin: false), token);

		Assert.Equal(["a-0", "b-0", "a-1", "b-1", "a-2", "b-2"], acquired.Select(static job => job.JobHandle.Value));
	}

	[Fact]
	public async Task ExactJobQueryComposesFiltersAndUsesCurrentQueue()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [new() { Name = "free", QueueName = "old" }] }, token);
		await storage.EnqueueAsync(Job("target", "free", group: null, clock.GetUtcNow()), token);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [new() { Name = "free", QueueName = "current" }] }, token);
		var query = new JobQuery
		{
			JobHandle = JobHandle.FromString("target"),
			State = JobState.Pending,
			JobName = "FREE",
			Search = "REE",
			QueueName = "current",
			CreatedBefore = clock.GetUtcNow().AddTicks(1),
		};

		Assert.Equal("current", Assert.Single(await storage.QueryJobsAsync(query, token)).QueueName);
		Assert.Equal("current", Assert.Single(await storage.QueryNonCompletedJobsAsync("FREE", token)).QueueName);
		JobQuery[] excluded =
		[
			query with { JobHandle = JobHandle.FromString("missing") },
			query with { State = JobState.Scheduled },
			query with { JobName = "other" },
			query with { Search = "other" },
			query with { QueueName = "old" },
			query with { CreatedBefore = clock.GetUtcNow() },
			query with { Skip = 1 },
		];
		foreach (var filter in excluded)
			Assert.Empty(await storage.QueryJobsAsync(filter, token));
	}

	private static JobSubmission Job(string id, string name, string? group, DateTimeOffset createdAt) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		GroupId = group,
		CreatedAt = createdAt,
		DueAt = DateTimeOffset.UnixEpoch,
	};

	private static JobAcquisitionRequest Request(int limitedCapacity, bool roundRobin) => new()
	{
		WorkerId = "worker",
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = 10,
		FairQueues = new() { GroupRoundRobin = roundRobin, ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = roundRobin ? 30 : 1 },
		Queues =
		[
			new()
			{
				QueueName = "default",
				Capacity = 10,
				JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { ["LIMITED"] = limitedCapacity, ["free"] = 10 },
			},
		],
	};
}

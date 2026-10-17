using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryStorageQueueIndexTests
{
	[Fact]
	public async Task QueueQueriesFollowCurrentDefinitionsAndComposeWithOtherFilters()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider());
		await RegisterAsync(storage, [new() { Name = "A", QueueName = "q" }, new() { Name = "B", QueueName = "Q" }], token);
		var a = Job("a", "a");
		var b = Job("b", "B");
		var orphan = Job("orphan", "removed") with { State = JobState.Succeeded };
		storage.LoadPersistedJobState([a, b, orphan], [], [], []);

		Assert.Equal(a.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { QueueName = "q" }, token)).JobHandle);
		Assert.Equal(b.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { QueueName = "Q", State = JobState.Pending }, token)).JobHandle);
		Assert.Empty(await storage.QueryJobsAsync(new() { QueueName = "q", JobName = "B" }, token));
		Assert.Empty(await storage.QueryJobsAsync(new() { QueueName = "Q", JobHandle = a.JobHandle }, token));
		Assert.Empty(await storage.QueryJobsAsync(new() { QueueName = "missing" }, token));

		await RegisterAsync(storage, [new() { Name = "A", QueueName = "moved" }, new() { Name = "B", QueueName = "moved" }], token);
		Assert.Equal(2, (await storage.QueryJobsAsync(new() { QueueName = "moved" }, token)).Count);
		Assert.Equal(a.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { QueueName = "moved", JobName = "A" }, token)).JobHandle);
		Assert.Empty(await storage.QueryJobsAsync(new() { QueueName = "q" }, token));

		await RegisterAsync(storage, [new() { Name = "B", QueueName = "moved" }], token);
		Assert.Equal(b.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { QueueName = "moved" }, token)).JobHandle);
		Assert.Equal(3, (await storage.QueryJobsAsync(new(), token)).Count);
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task AcquisitionOnlyClaimsRequestedDefinitionsInTheExactQueue(bool fair, bool grouped)
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider());
		await RegisterAsync(storage,
		[
			new() { Name = "wanted", QueueName = "q" },
			new() { Name = "unrequested", QueueName = "q" },
			new() { Name = "other", QueueName = "Q" },
			new() { Name = "empty-a", QueueName = "q" },
			new() { Name = "empty-b", QueueName = "q" },
		], token);
		var wanted = Job("wanted", "wanted") with { GroupId = grouped ? "group" : null };
		storage.LoadPersistedJobState([Job("unrequested", "unrequested"), Job("other", "other"), wanted], [], [], []);
		var request = Request(fair) with
		{
			Queues = [new()
			{
				QueueName = "q",
				Capacity = 10,
				JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
				{
					["WANTED"] = 1, ["other"] = 10, ["empty-a"] = 1, ["empty-b"] = 1,
				},
			}],
		};

		Assert.Equal(wanted.JobHandle, Assert.Single(await storage.AcquireDueJobsAsync(request, token)).JobHandle);
		Assert.Equal(2, (await storage.QueryJobsAsync(new() { State = JobState.Pending }, token)).Count);
	}

	[Fact]
	public async Task FairUsageIncludesUnrequestedDefinitionsButExcludesOtherQueues()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider());
		await RegisterAsync(storage,
		[
			new() { Name = "wanted", QueueName = "q" },
			new() { Name = "unrequested", QueueName = "q" },
			new() { Name = "other", QueueName = "Q" },
		], token);
		var first = Job("first", "wanted") with { GroupId = "a" };
		var second = Job("second", "wanted") with { GroupId = "b", CreatedAt = DateTimeOffset.UnixEpoch.AddTicks(1) };
		storage.LoadPersistedJobState(
		[
			Active("a-1", "unrequested", "a"), Active("a-2", "unrequested", "a"),
			Active("b-1", "other", "b"), Active("b-2", "other", "b"), Active("b-3", "other", "b"),
			first, second,
		], [], [], []);

		Assert.Equal(second.JobHandle, Assert.Single(await storage.AcquireDueJobsAsync(Request(fair: true), token)).JobHandle);
	}

	private static ValueTask RegisterAsync(InMemoryJobStorage storage, IReadOnlyList<JobDefinitionRecord> definitions, CancellationToken token) =>
		storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = definitions,
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);

	private static JobRecord Job(string id, string name) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		CreatedAt = DateTimeOffset.UnixEpoch,
		DueAt = DateTimeOffset.UnixEpoch,
	};

	private static JobRecord Active(string id, string name, string group) => Job(id, name) with
	{
		State = JobState.Active,
		GroupId = group,
		WorkerId = "worker",
		LeaseExpiresAt = DateTimeOffset.MaxValue,
	};

	private static JobAcquisitionRequest Request(bool fair) => new()
	{
		WorkerId = "worker",
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = 1,
		FairQueues = fair ? new() { GroupRoundRobin = false, ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = 1 } : null,
		Queues = [new()
		{
			QueueName = "q", Capacity = 1,
			JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wanted"] = 1 },
		}],
	};
}

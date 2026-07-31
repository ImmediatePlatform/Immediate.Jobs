using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class QueueStorageTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("{\"usage\":{\"userId\":\"42\"}}")]
	public async Task ContextRoundTripsThroughInMemoryStorage(string? context)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var job = new JobRecord
		{
			Id = Guid.NewGuid().ToString("N"),
			JobName = "context-test",
			Payload = "{}",
			Context = context,
			State = JobState.Pending,
			DueAt = clock.GetUtcNow(),
			CreatedAt = clock.GetUtcNow(),
		};

		await storage.EnqueueAsync(job, cancellationToken);

		var queried = Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken));
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "context-worker",
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = 1,
			Queues =
			[
				new()
				{
					QueueName = JobQueueDefinition.DefaultName,
					Capacity = 1,
					JobCapacities = new Dictionary<string, int> { [job.JobName] = 1 },
				},
			],
		}, cancellationToken));
		Assert.Equal(context, queried.Context);
		Assert.Equal(context, acquired.Context);
	}

	[Fact]
	public async Task RetryFastForwardsScheduledJobs()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var job = new JobRecord
		{
			Id = "scheduled-retry",
			JobName = "retry-test",
			Payload = "{}",
			State = JobState.Scheduled,
			DueAt = clock.GetUtcNow().AddHours(1),
			CreatedAt = clock.GetUtcNow(),
			Attempt = 1,
			LastError = "expected failure",
		};
		await storage.EnqueueAsync(job, cancellationToken);

		await storage.RetryAsync(job.Id, cancellationToken);

		var retried = Assert.Single(await storage.QueryJobsAsync(new() { Id = job.Id }, cancellationToken));
		Assert.Equal(JobState.Pending, retried.State);
		Assert.Equal(clock.GetUtcNow(), retried.DueAt);
		Assert.Equal(1, retried.Attempt);
		Assert.Equal(job.LastError, retried.LastError);

		var firstRun = job with { Id = "scheduled-first-run", Attempt = 0, LastError = null };
		await storage.EnqueueAsync(firstRun, cancellationToken);
		await storage.RetryAsync(firstRun.Id, cancellationToken);
		var fastForwarded = Assert.Single(await storage.QueryJobsAsync(new() { Id = firstRun.Id }, cancellationToken));
		Assert.Equal(JobState.Pending, fastForwarded.State);
		Assert.Equal(clock.GetUtcNow(), fastForwarded.DueAt);
		Assert.Equal(0, fastForwarded.Attempt);
	}

	[Fact]
	public async Task AcquisitionHonorsQueueOrderAndQueueAndJobCapacities()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.InitializeAsync(cancellationToken);

		await Enqueue("low", "low-job", 0);
		await Enqueue("high", "limited-job", 1);
		await Enqueue("high", "limited-job", 2);
		await Enqueue("high", "other-job", 3);

		var acquired = await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "node-a",
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = 3,
			Queues =
			[
				new()
				{
					QueueName = "high",
					Capacity = 2,
					JobCapacities = new Dictionary<string, int> { ["limited-job"] = 1, ["other-job"] = 1 },
				},
				new()
				{
					QueueName = "low",
					Capacity = 1,
					JobCapacities = new Dictionary<string, int> { ["low-job"] = 1 },
				},
			],
		}, cancellationToken);

		Assert.Equal(["limited-job", "other-job", "low-job"], acquired.Select(static job => job.JobName));
		Assert.All(
			await storage.QueryJobsAsync(new() { QueueName = "high" }, cancellationToken),
			static job => Assert.Equal("high", job.QueueName)
		);

		ValueTask Enqueue(string queueName, string jobName, int order) => storage.EnqueueAsync(new()
		{
			Id = Guid.NewGuid().ToString("N"),
			QueueName = queueName,
			JobName = jobName,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = clock.GetUtcNow(),
			CreatedAt = clock.GetUtcNow().AddTicks(order),
		}, cancellationToken);
	}
}
#pragma warning restore CS1591

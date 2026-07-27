using Immediate.Jobs.Redis;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Immediate.Jobs.StorageTests;

#pragma warning disable CS1591
[CollectionDefinition(Name)]
public sealed class RedisContainerFixtureGroup : ICollectionFixture<RedisStorageFixture>
{
	public const string Name = "Redis storage";
}

public sealed class RedisStorageFixture : IAsyncLifetime
{
	public RedisContainer Container { get; } = new RedisBuilder("redis:8-alpine").Build();

	public ValueTask InitializeAsync() => new(Container.StartAsync());

	public ValueTask DisposeAsync() => Container.DisposeAsync();
}

[Collection(RedisContainerFixtureGroup.Name)]
public sealed class RedisStorageTests(RedisStorageFixture fixture)
{
	[Fact]
	public async Task ClaimsEachJobOnceUnderContention()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		using var first = new RedisJobStorage(connection, options, timeProvider);
		using var second = new RedisJobStorage(connection, options, timeProvider);
		foreach (var index in Enumerable.Range(0, 24))
			await first.EnqueueAsync(CreateJob($"contended-{index}", timeProvider.GetUtcNow()), cancellationToken);

		var claims = await Task.WhenAll(
			first.AcquireDueJobsAsync(CreateRequest("node-a", 24), cancellationToken).AsTask(),
			second.AcquireDueJobsAsync(CreateRequest("node-b", 24), cancellationToken).AsTask()
		);

		var claimed = claims.SelectMany(static claim => claim).ToArray();
		Assert.Equal(24, claimed.Length);
		Assert.Equal(24, claimed.Select(static job => job.Id).Distinct(StringComparer.Ordinal).Count());
		Assert.All(claimed, static job => Assert.Equal(JobState.Active, job.State));
	}

	[Fact]
	public async Task ExpiredLeaseIsRecoveredAndRejectsTheStaleOwner()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		using var first = new RedisJobStorage(connection, options, timeProvider);
		using var second = new RedisJobStorage(connection, options, timeProvider);
		await first.EnqueueAsync(
			CreateJob("leased", timeProvider.GetUtcNow()) with { GroupId = "tenant-a" },
			cancellationToken
		);

		var firstClaim = Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		Assert.Equal("tenant-a", firstClaim.GroupId);
		await first.SetExecutionTelemetryAsync(
			"leased",
			"node-a",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			timeProvider.GetUtcNow(),
			cancellationToken
		);
		timeProvider.Advance(TimeSpan.FromSeconds(59));
		Assert.Empty(await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));

		timeProvider.Advance(TimeSpan.FromSeconds(2));
		var recovered = Assert.Single(await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-b", recovered.WorkerId);
		Assert.Equal("tenant-a", recovered.GroupId);
		Assert.Null(recovered.ExecutionTraceId);
		Assert.Null(recovered.ExecutionSpanId);
		Assert.Null(recovered.ExecutionStartedAt);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => first.CompleteAsync("leased", "node-a", cancellationToken).AsTask()
		);
	}

	[Fact]
	public async Task ExpiredLeaseRemainsRecoverableWhenItsQueueIsTemporarilyOmitted()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		using var storage = new RedisJobStorage(connection, options, timeProvider);
		await storage.EnqueueAsync(
			CreateJob("secondary", timeProvider.GetUtcNow()) with { QueueName = "secondary" },
			cancellationToken
		);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("node-a", 1, "secondary"),
			cancellationToken
		));
		timeProvider.Advance(TimeSpan.FromMinutes(2));

		Assert.Empty(await storage.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));
		var recovered = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("node-b", 1, "secondary"),
			cancellationToken
		));
		Assert.Equal("secondary", recovered.Id);
		Assert.Equal(2, recovered.Attempt);
	}

	[Fact]
	public async Task ConcurrentRecurringMaterializationCreatesOneOccurrence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		using var first = new RedisJobStorage(connection, options, timeProvider);
		using var second = new RedisJobStorage(connection, options, timeProvider);
		var now = timeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "hourly",
			JobName = "test-job",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		await first.UpsertRecurringAsync(schedule, cancellationToken);
		var recurringKey = $"{schedule.Name}:{schedule.NextRunAt.UtcTicks}";

		var results = await Task.WhenAll(
			first.MaterializeRecurringAsync(
				schedule,
				CreateJob("occurrence-a", now) with { RecurringKey = recurringKey },
				now.AddHours(1),
				cancellationToken
			).AsTask(),
			second.MaterializeRecurringAsync(
				schedule,
				CreateJob("occurrence-b", now) with { RecurringKey = recurringKey },
				now.AddHours(1),
				cancellationToken
			).AsTask()
		);

		_ = Assert.Single(results, static inserted => inserted);
		var jobs = await first.QueryJobsAsync(new() { Search = "test-job" }, cancellationToken);
		_ = Assert.Single(jobs);
		var persisted = Assert.Single((await first.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
		Assert.Equal(now, persisted.LastRunAt);
		Assert.Equal(now.AddHours(1), persisted.NextRunAt);
		Assert.Equal(StorageCapabilities.Queue | StorageCapabilities.Recurring, first.GetCapabilities());
		Assert.IsNotAssignableFrom<IJobGraphStorage>(first);
	}

	[Fact]
	public async Task RecurringMaterializationPersistsSkippedOccurrenceAsCancelled()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		using var storage = new RedisJobStorage(connection, CreateOptions(), timeProvider);
		var now = timeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "skip-overlap",
			JobName = "test-job",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);

		Assert.True(await storage.MaterializeRecurringAsync(
			schedule,
			CreateJob("skipped", now) with
			{
				State = JobState.Cancelled,
				CompletedAt = now,
				RecurringKey = $"{schedule.Name}:{now.UtcTicks}",
			},
			now.AddHours(1),
			cancellationToken
		));
		var skipped = Assert.Single(await storage.QueryJobsAsync(
			new() { State = JobState.Cancelled },
			cancellationToken
		));
		Assert.Equal(now, skipped.CompletedAt);
		Assert.Empty(await storage.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
	}

	[Fact]
	public async Task UseRedisSelectsDistributedMode()
	{
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var options = new ImmediateJobsOptions();
		_ = options.UseRedis(connection, redis => redis.KeyPrefix = $"test:{Guid.NewGuid():N}");
		Assert.Equal(JobStorageMode.Distributed, options.StorageMode);
	}

	[Fact]
	public async Task DisposingStorageDoesNotCloseAnApplicationOwnedConnection()
	{
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		await using var storage = new RedisJobStorage(connection, CreateOptions(), CreateTimeProvider());

		await storage.DisposeAsync();

		Assert.True(connection.IsConnected);
		_ = await connection.GetDatabase().PingAsync();
	}

	[Fact]
	public async Task FairQueueAcquisitionIsExplicitlyUnsupported()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		using var storage = new RedisJobStorage(connection, CreateOptions(), CreateTimeProvider());
		var request = CreateRequest("worker", 1) with
		{
			FairQueues = new(0.10, 30, true),
		};

		var exception = await Assert.ThrowsAsync<NotSupportedException>(
			() => storage.AcquireDueJobsAsync(request, cancellationToken).AsTask()
		);
		Assert.Contains("Redis", exception.Message, StringComparison.Ordinal);
	}

	private static FakeTimeProvider CreateTimeProvider() =>
		new(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

	private static RedisJobStorageOptions CreateOptions() =>
		new() { KeyPrefix = $"immediate-jobs-test-{Guid.NewGuid():N}" };

	private static JobRecord CreateJob(string id, DateTimeOffset now) => new()
	{
		Id = id,
		JobName = "test-job",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now,
	};

	private static JobAcquisitionRequest CreateRequest(
		string workerId,
		int capacity,
		string queueName = JobQueueDefinition.DefaultName
	) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = capacity,
		Queues =
		[
			new()
			{
				QueueName = queueName,
				Capacity = capacity,
				JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal)
				{
					["test-job"] = capacity,
				},
			},
		],
	};
}
#pragma warning restore CS1591

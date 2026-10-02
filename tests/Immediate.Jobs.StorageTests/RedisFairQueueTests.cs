using Immediate.Jobs.Redis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace Immediate.Jobs.StorageTests;

[Collection(RedisContainerFixtureGroup.Name)]
public sealed class RedisFairQueueTests(RedisStorageFixture redis)
{
	private const string QueueName = "default";
	private const string JobName = "redis-fair-job";

	private static readonly FairQueuePolicy Policy = new()
	{
		ConcurrencyShareThreshold = 0.10,
		MinInflightForNoisy = 30,
		GroupRoundRobin = true,
	};

	[Fact]
	public async Task InitializeIndexesJobsEnqueuedBeforeFairQueueSupport()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var context = await RedisTestContext.CreateAsync(redis.Container.GetConnectionString());
		await context.EnqueueAsync("legacy-a-1", 0, "group-a", cancellationToken);
		await context.EnqueueAsync("legacy-a-2", 1, "group-a", cancellationToken);
		await context.EnqueueAsync("legacy-b-1", 2, "group-b", cancellationToken);

		await context.RemoveFairQueueStateAsync(["legacy-a-1", "legacy-a-2", "legacy-b-1"]);
		await context.Storage.InitializeAsync(cancellationToken);

		var acquired = await context.Storage.AcquireDueJobsAsync(CreateRequest("legacy-worker", 3), cancellationToken);

		Assert.Equal(
			["legacy-a-1", "legacy-b-1", "legacy-a-2"],
			acquired.Select(static job => job.JobHandle.Value)
		);
	}

	[Fact]
	public async Task RetriedJobsRemainFairlyAcquirable()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var context = await RedisTestContext.CreateAsync(redis.Container.GetConnectionString());
		await context.EnqueueAsync("retry-a-1", 0, "group-a", cancellationToken);
		await context.EnqueueAsync("retry-b-1", 1, "group-b", cancellationToken);

		var first = Assert.Single(await context.Storage.AcquireDueJobsAsync(CreateRequest("retry-worker-a", 1), cancellationToken));
		await context.Storage.FailAsync(
			first.JobHandle,
			first.Attempt,
			"retry-worker-a",
			"transient",
			context.Clock.GetUtcNow(),
			cancellationToken
		);

		var second = Assert.Single(await context.Storage.AcquireDueJobsAsync(CreateRequest("retry-worker-b", 1), cancellationToken));
		await context.Storage.FailAsync(
			second.JobHandle,
			second.Attempt,
			"retry-worker-b",
			"terminal",
			nextRetryAt: null,
			cancellationToken
		);
		await context.Storage.RetryAsync(second.JobHandle, cancellationToken);

		var remaining = await context.Storage.AcquireDueJobsAsync(CreateRequest("retry-worker-c", 2), cancellationToken);

		Assert.Equal("retry-a-1", first.JobHandle.Value);
		Assert.Equal("retry-b-1", second.JobHandle.Value);
		Assert.Equal(
			["retry-a-1", "retry-b-1"],
			remaining.Select(static job => job.JobHandle.Value).Order(StringComparer.Ordinal)
		);
	}

	[Fact]
	public async Task CancelledJobsLeaveTheFairIndexAndClearTheirCursor()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var context = await RedisTestContext.CreateAsync(redis.Container.GetConnectionString());
		await context.EnqueueAsync("cancel-a-1", 0, "group-a", cancellationToken);
		await context.EnqueueAsync("cancel-a-2", 1, "group-a", cancellationToken);
		await context.EnqueueAsync("cancel-b-1", 2, "group-b", cancellationToken);

		var first = Assert.Single(await context.Storage.AcquireDueJobsAsync(CreateRequest("cancel-worker-a", 1), cancellationToken));
		Assert.True(await context.HasCursorAsync("group-a"));

		await context.Storage.CancelAsync(first.JobHandle, cancellationToken);
		await context.Storage.CancelAsync(JobHandle.FromString("cancel-a-2"), cancellationToken);

		Assert.False(await context.HasCursorAsync("group-a"));
		var remaining = await context.Storage.AcquireDueJobsAsync(CreateRequest("cancel-worker-b", 3), cancellationToken);
		Assert.Equal("cancel-b-1", Assert.Single(remaining).JobHandle.Value);
	}

	private static JobAcquisitionRequest CreateRequest(string workerId, int batchSize) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = batchSize,
		FairQueues = Policy,
		Queues =
		[
			new()
			{
				QueueName = QueueName,
				Capacity = batchSize,
				JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal)
				{
					[JobName] = batchSize,
				},
			},
		],
	};

	private sealed class RedisTestContext : IAsyncDisposable
	{
		private readonly IConnectionMultiplexer _connection;
		private readonly ServiceProvider _services;
		private readonly string _root;

		private RedisTestContext(IConnectionMultiplexer connection, ServiceProvider services, FakeTimeProvider clock, string root)
		{
			_connection = connection;
			_services = services;
			_root = root;
			Clock = clock;
			Storage = services.GetRequiredService<IJobStorage>();
		}

		public FakeTimeProvider Clock { get; }

		public IJobStorage Storage { get; }

		public static async ValueTask<RedisTestContext> CreateAsync(string connectionString)
		{
			var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
			var keyPrefix = "immediate-jobs-fair-" + Guid.NewGuid().ToString("N");
			var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero));

			var services = new ServiceCollection();
			services.AddLogging();
			services.AddSingleton<TimeProvider>(clock);
			services.AddSingleton<IConnectionMultiplexer>(connection);
			services
				.AddImmediateJobsCore()
				.ConfigureStorage(options =>
					options.UseRedis()
						.ConfigureRedis(storage => storage.KeyPrefix = keyPrefix)
				);

			return new(connection, services.BuildServiceProvider(), clock, $"{{{keyPrefix}}}:");
		}

		public ValueTask EnqueueAsync(string id, int offsetSeconds, string groupId, CancellationToken cancellationToken)
		{
			var at = Clock.GetUtcNow().AddSeconds(offsetSeconds - 60);
			return Storage.EnqueueAsync(new()
			{
				JobHandle = JobHandle.FromString(id),
				JobName = JobName,
				QueueName = QueueName,
				GroupId = groupId,
				Payload = "{}",
				State = JobState.Pending,
				DueAt = at,
				CreatedAt = at,
			}, cancellationToken);
		}

		public async ValueTask RemoveFairQueueStateAsync(IReadOnlyList<string> ids)
		{
			var database = _connection.GetDatabase();
			foreach (var id in ids)
				_ = await database.HashDeleteAsync(_root + "job:" + id, "group");

			var server = _connection.GetServer(_connection.GetEndPoints()[0]);
			await foreach (var key in server.KeysAsync(pattern: _root + "fair:*"))
				_ = await database.KeyDeleteAsync(key);
		}

		public async ValueTask<bool> HasCursorAsync(string groupId) =>
			await _connection.GetDatabase().HashExistsAsync(_root + "fair:cursor:" + QueueName, groupId);

		public async ValueTask DisposeAsync()
		{
			var database = _connection.GetDatabase();
			var server = _connection.GetServer(_connection.GetEndPoints()[0]);
			await foreach (var key in server.KeysAsync(pattern: _root + "*"))
				_ = await database.KeyDeleteAsync(key);

			await _services.DisposeAsync();
			await _connection.DisposeAsync();
		}
	}
}

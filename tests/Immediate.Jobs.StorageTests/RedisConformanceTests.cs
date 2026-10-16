using Immediate.Jobs.Redis;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Immediate.Jobs.StorageTests;

[Collection(RedisContainerFixtureGroup.Name)]
public sealed class RedisConformanceTests(RedisStorageFixture redis)
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue;

	public static TheoryData<JobStorageConformanceTestCase> Cases =>
		[.. JobStorageConformanceSuite.GetCases(Capabilities)];

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task RedisConforms(JobStorageConformanceTestCase testCase)
	{
		ArgumentNullException.ThrowIfNull(testCase);

		await using var fixture = await RedisConformanceFixture.CreateAsync(
			redis.Container.GetConnectionString(),
			testCase.PersistedJobState
		);

		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task LegacyQueueMetadataSupportsAcquisitionAndCatalogueUpdates()
	{
		var token = TestContext.Current.CancellationToken;
		await using var fixture = await RedisConformanceFixture.CreateAsync(
			redis.Container.GetConnectionString(),
			new() { Jobs = [], Batches = [], Edges = [], RecurringSchedules = [] }
		);
		await fixture.SetDefinitionMetadataAsync("legacy", """
			{"Name":"legacy","QueueName":"old-queue","QueuePriority":9,"QueueConcurrency":2,"Tags":["default"]}
			""");
		var storage = fixture.Services.GetRequiredService<IJobStorage>();
		var definition = Assert.Single(await storage.GetJobDefinitionsAsync(token));
		Assert.Equal(new() { Name = "old-queue", Priority = 9, Concurrency = 2 }, definition.Queue);
		var now = fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow();
		foreach (var queue in new[] { "old-queue", "new-queue" })
		{
			if (string.Equals(queue, "new-queue", StringComparison.Ordinal))
			{
				definition = definition with { Queue = definition.Queue with { Name = queue } };
				await storage.MergeJobDefinitionsListAsync(new() { Definitions = [definition] }, token);
			}

			var handle = JobHandle.FromString(queue);
			await storage.EnqueueAsync(new()
			{
				JobHandle = handle,
				JobName = definition.Name,
				GroupId = "tenant",
				Payload = "{}",
				State = JobState.Pending,
				DueAt = now,
				CreatedAt = now,
			}, token);
			var acquired = Assert.Single(await storage.AcquireDueJobsAsync(new()
			{
				WorkerId = "worker",
				Lease = TimeSpan.FromMinutes(1),
				BatchSize = 1,
				Queues = [new() { QueueName = queue, Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [definition.Name] = 1 } }],
				FairQueues = new() { ConcurrencyShareThreshold = 0.1, MinInflightForNoisy = 30, GroupRoundRobin = true },
			}, token));
			Assert.Equal(handle, acquired.JobHandle);
			Assert.Equal(queue, acquired.QueueName);
			await storage.CompleteAsync(handle, acquired.Attempt, "worker", token);
		}
	}
}

file sealed class RedisConformanceFixture : IAsyncDisposable
{
	private readonly IConnectionMultiplexer _connection;
	private readonly string _keyPrefix;

	private RedisConformanceFixture(
		IConnectionMultiplexer connection,
		string keyPrefix,
		ServiceProvider services
	)
	{
		_connection = connection;
		_keyPrefix = keyPrefix;
		Services = services;
	}

	internal IServiceProvider Services { get; }

	internal Task SetDefinitionMetadataAsync(string name, string json) =>
		_connection.GetDatabase().HashSetAsync($"{{{_keyPrefix}}}:definition-metadata", name.ToUpperInvariant(), json);

	internal static async ValueTask<RedisConformanceFixture> CreateAsync(
		string connectionString,
		PersistedJobState persistedJobState
	)
	{
		var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);

		try
		{
			var keyPrefix = "immediate-jobs-conformance-" + Guid.NewGuid().ToString("N");
			var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero));

			var serviceCollection = new ServiceCollection();

			serviceCollection.AddLogging();
			serviceCollection.AddSingleton<TimeProvider>(clock);
			serviceCollection.AddSingleton(clock);
			serviceCollection.AddSingleton<IConnectionMultiplexer>(connection);

			serviceCollection
				.AddImmediateJobsCore()
				.ConfigureStorage(options =>
					options.UseRedis()
						.ConfigureRedis(storage => storage.KeyPrefix = keyPrefix)
				);

			var services = serviceCollection.BuildServiceProvider(
				new ServiceProviderOptions
				{
					ValidateOnBuild = true,
					ValidateScopes = true,
				}
			);

			await LoadJobs(services, persistedJobState.Jobs);
			await LoadRecurringSchedules(services, persistedJobState.RecurringSchedules);

			return new(connection, keyPrefix, services);
		}
		catch
		{
			await connection.DisposeAsync();
			throw;
		}
	}

	private static async ValueTask LoadJobs(IServiceProvider serviceProvider, IReadOnlyList<JobRecord> jobs)
	{
		if (jobs is [])
			return;

		var storage = serviceProvider.GetRequiredService<IJobStorage>();

		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = jobs.DistinctBy(static job => job.JobName, StringComparer.OrdinalIgnoreCase)
				.Select(static job => new JobDefinition { Name = job.JobName, Queue = new() { Name = job.QueueName ?? "default" } }).ToList(),
		});
		foreach (var job in jobs)
			await storage.EnqueueAsync(new()
			{
				JobHandle = job.JobHandle,
				JobName = job.JobName,
				Payload = job.Payload,
				State = job.State,
				DueAt = job.DueAt,
				CreatedAt = job.CreatedAt,
				RecurringKey = job.RecurringKey,
			});
	}

	private static async ValueTask LoadRecurringSchedules(
		IServiceProvider serviceProvider,
		IReadOnlyList<RecurringJobSchedule> schedules
	)
	{
		if (schedules is [])
			return;

		var storage = serviceProvider.GetRequiredService<IJobStorage>();
		foreach (var schedule in schedules)
			await storage.UpsertRecurringAsync(schedule);
	}

	public async ValueTask DisposeAsync()
	{
		await ((ServiceProvider)Services).DisposeAsync();
		foreach (var endpoint in _connection.GetEndPoints())
		{
			var server = _connection.GetServer(endpoint);
			var keys = new List<RedisKey>();
			await foreach (var key in server.KeysAsync(pattern: $"{{{_keyPrefix}}}:*"))
				keys.Add(key);
			if (keys.Count > 0)
				_ = await _connection.GetDatabase().KeyDeleteAsync([.. keys], flags: CommandFlags.None);
		}

		await _connection.DisposeAsync();
	}
}

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

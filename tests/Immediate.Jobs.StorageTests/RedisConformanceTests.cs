using System.Globalization;
using System.Text.Json;
using Immediate.Jobs.Redis;
using Immediate.Jobs.Shared.Apis;
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

			await LoadJobs(services, connection.GetDatabase(), keyPrefix, persistedJobState.Jobs);
			await LoadRecurringSchedules(services, persistedJobState.RecurringSchedules);

			return new(connection, keyPrefix, services);
		}
		catch
		{
			await connection.DisposeAsync();
			throw;
		}
	}

	private static async ValueTask LoadJobs(IServiceProvider serviceProvider, IDatabase database, string keyPrefix, IReadOnlyList<JobRecord> jobs)
	{
		if (jobs is [])
			return;

		var storage = serviceProvider.GetRequiredService<IJobStorage>();

		await storage.MergeJobDefinitionsListAsync(new()
		{
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
			Definitions = jobs.DistinctBy(static job => job.JobName, StringComparer.OrdinalIgnoreCase)
				.Select(static job => new JobDefinitionRecord { Name = job.JobName, QueueName = job.QueueName ?? "default" }).ToList(),
		});
		var root = $"{{{keyPrefix}}}:";
		foreach (var job in jobs)
		{
			var initialState = job.State is JobState.Pending or JobState.Scheduled or JobState.WaitingForTrigger
				? job.State : JobState.WaitingForTrigger;
			await storage.EnqueueAsync(new()
			{
				JobHandle = job.JobHandle,
				JobName = job.JobName,
				Payload = job.Payload,
				State = initialState,
				GroupId = job.GroupId,
				DueAt = job.DueAt,
				CreatedAt = job.CreatedAt,
				RecurringKey = job.RecurringKey,
			});
			await database.HashSetAsync(root + "job:" + job.JobHandle.Value,
			[
				new("record", JsonSerializer.Serialize(job, RedisJsonSerializerContext.Default.JobRecord)),
				new("state", (int)job.State), new("attempt", job.Attempt),
				new("worker", job.WorkerId ?? ""), new("lease", Timestamp(job.LeaseExpiresAt)),
				new("error", job.LastError ?? ""), new("completed", Timestamp(job.CompletedAt)),
				new("executionTraceId", job.ExecutionTraceId ?? ""), new("executionSpanId", job.ExecutionSpanId ?? ""),
				new("executionStartedAt", Timestamp(job.ExecutionStartedAt)), new("cancellationReason", job.CancellationReason ?? ""),
			]);
			if (initialState != job.State)
			{
				await database.SetRemoveAsync(string.Create(CultureInfo.InvariantCulture, $"{root}state:{(int)initialState}"), job.JobHandle.Value);
				await database.SetAddAsync(string.Create(CultureInfo.InvariantCulture, $"{root}state:{(int)job.State}"), job.JobHandle.Value);
			}

			if (job.CompletedAt is { } completed)
				await database.SortedSetAddAsync(string.Create(CultureInfo.InvariantCulture, $"{root}completed:{(int)job.State}"), job.JobHandle.Value, completed.ToUnixTimeMilliseconds());
			if (job.LeaseExpiresAt is { } lease)
				await database.SortedSetAddAsync(root + "leases", job.JobHandle.Value, lease.ToUnixTimeMilliseconds());
		}

		static string Timestamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
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

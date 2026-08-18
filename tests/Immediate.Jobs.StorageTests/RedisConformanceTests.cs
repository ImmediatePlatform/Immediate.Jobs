using Immediate.Jobs.Redis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace Immediate.Jobs.StorageTests;

[Collection(RedisContainerFixtureGroup.Name)]
public sealed class RedisConformanceTests(RedisStorageFixture redis)
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring;

	public static TheoryData<JobStorageConformanceTestCase> Cases =>
		[.. JobStorageConformanceSuite.GetCases(Capabilities)];

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task RedisConforms(JobStorageConformanceTestCase testCase)
	{
		ArgumentNullException.ThrowIfNull(testCase);

		await using var fixture = await RedisConformanceFixture.CreateAsync(
			redis.Container.GetConnectionString(),
			TestContext.Current.CancellationToken
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
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var connection = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
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

			var services = serviceCollection.BuildServiceProvider(new ServiceProviderOptions
			{
				ValidateOnBuild = true,
				ValidateScopes = true,
			});
			return new(connection, keyPrefix, services);
		}
		catch
		{
			await connection.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public async ValueTask DisposeAsync()
	{
		await ((ServiceProvider)Services).DisposeAsync().ConfigureAwait(false);
		foreach (var endpoint in _connection.GetEndPoints())
		{
			var server = _connection.GetServer(endpoint);
			var keys = new List<RedisKey>();
			await foreach (var key in server.KeysAsync(pattern: $"{{{_keyPrefix}}}:*").ConfigureAwait(false))
				keys.Add(key);
			if (keys.Count > 0)
				_ = await _connection.GetDatabase().KeyDeleteAsync([.. keys], flags: CommandFlags.None).ConfigureAwait(false);
		}

		await _connection.DisposeAsync().ConfigureAwait(false);
	}
}

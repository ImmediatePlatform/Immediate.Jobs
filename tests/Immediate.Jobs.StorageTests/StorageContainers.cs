using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Immediate.Jobs.StorageTests;

[CollectionDefinition(Name)]
public sealed class StorageContainerFixtureGroup : ICollectionFixture<StorageContainers>
{
	public const string Name = "Storage containers";
}

public sealed class StorageContainers : IAsyncLifetime
{
	public PostgreSqlContainer PostgreSql { get; } = new PostgreSqlBuilder("postgres:18-alpine").Build();
	public MsSqlContainer SqlServer { get; } = new MsSqlBuilder(
		"mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04"
	).Build();

	public async ValueTask InitializeAsync()
	{
		await Task.WhenAll(PostgreSql.StartAsync(), SqlServer.StartAsync());
	}

	public async ValueTask DisposeAsync()
	{
		await Task.WhenAll(
			PostgreSql.DisposeAsync().AsTask(),
			SqlServer.DisposeAsync().AsTask()
		);
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

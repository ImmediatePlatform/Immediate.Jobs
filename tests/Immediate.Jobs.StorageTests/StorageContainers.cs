using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Immediate.Jobs.StorageTests;

#pragma warning disable CS1591
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
		await PostgreSql.DisposeAsync();
		await SqlServer.DisposeAsync();
	}
}
#pragma warning restore CS1591

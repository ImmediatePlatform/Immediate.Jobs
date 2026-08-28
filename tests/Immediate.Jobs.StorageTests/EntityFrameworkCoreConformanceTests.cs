using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Containers;
using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Immediate.Jobs.StorageTests;

public static class EntityFrameworkCoreConformanceTestCases
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring |
		StorageCapabilities.Graph |
		StorageCapabilities.FairQueues |
		StorageCapabilities.Replica;

	public static TheoryData<ConformanceTopology, JobStorageConformanceTestCase> CreateCases()
	{
		var data = new TheoryData<ConformanceTopology, JobStorageConformanceTestCase>();
		foreach (var topology in Enum.GetValues<ConformanceTopology>())
		{
			var capabilities = topology == ConformanceTopology.Distributed
				? Capabilities
				: Capabilities & ~StorageCapabilities.Replica;

			foreach (var testCase in JobStorageConformanceSuite.GetCases(capabilities))
				data.Add(topology, testCase);
		}

		return data;
	}
}

[Collection(EntityFrameworkCorePgSQLFixtureGroup.Name)]
public sealed class EntityFrameworkCorePgSQLConformanceTests(EntityFrameworkCorePgSQLContainer container)
{
	[Theory]
	[MemberData(nameof(EntityFrameworkCoreConformanceTestCases.CreateCases), MemberType = typeof(EntityFrameworkCoreConformanceTestCases))]
	public async Task EntityFrameworkCoreConforms(
		ConformanceTopology topology,
		JobStorageConformanceTestCase testCase
	)
	{
		await using var fixture = await RelationalConformanceFixture.CreateAsync(
			ConformanceDatabase.PostgreSql,
			useDistributedTopology: topology == ConformanceTopology.Distributed,
			container: container.PostgreSql
		);

		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}
}

[Collection(EntityFrameworkCoreMsSQLFixtureGroup.Name)]
public sealed class EntityFrameworkCoreMsSQLConformanceTests(EntityFrameworkCoreMsSQLContainer container)
{
	[Theory]
	[MemberData(nameof(EntityFrameworkCoreConformanceTestCases.CreateCases), MemberType = typeof(EntityFrameworkCoreConformanceTestCases))]
	public async Task EntityFrameworkCoreConforms(
		ConformanceTopology topology,
		JobStorageConformanceTestCase testCase
	)
	{
		await using var fixture = await RelationalConformanceFixture.CreateAsync(
			ConformanceDatabase.SqlServer,
			useDistributedTopology: topology == ConformanceTopology.Distributed,
			container: container.SqlServer
		);

		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}
}

[Collection("EntityFrameworkCore-SQLite")]
public sealed class EntityFrameworkCoreSQLiteConformanceTests
{
	[Theory]
	[MemberData(nameof(EntityFrameworkCoreConformanceTestCases.CreateCases), MemberType = typeof(EntityFrameworkCoreConformanceTestCases))]
	public async Task EntityFrameworkCoreConforms(
		ConformanceTopology topology,
		JobStorageConformanceTestCase testCase
	)
	{
		await using var fixture = await RelationalConformanceFixture.CreateAsync(
			ConformanceDatabase.Sqlite,
			useDistributedTopology: topology == ConformanceTopology.Distributed,
			container: null
		);

		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}
}

file sealed class RelationalConformanceFixture(
	ConformanceDatabase database,
	ServiceProvider services,
	string connectionString,
	string? schema,
	string? sqlitePath
) : IAsyncDisposable
{
	private static readonly string[] SqlServerTables =
	[
		"immediate_job_continuations",
		"immediate_job_executions",
		"immediate_fair_queue_groups",
		"immediate_jobs",
		"immediate_job_batches",
		"immediate_recurring_jobs",
		"immediate_job_servers",
	];

	internal IServiceProvider Services => services;

	internal static async ValueTask<RelationalConformanceFixture> CreateAsync(
		ConformanceDatabase database,
		bool useDistributedTopology,
		IDatabaseContainer? container
	)
	{
		var schema = database == ConformanceDatabase.Sqlite ? null : "jobs_" + Guid.NewGuid().ToString("N");
		var sqlitePath = database == ConformanceDatabase.Sqlite
			? Path.Combine(Path.GetTempPath(), $"immediate-jobs-conformance-{Guid.NewGuid():N}.db")
			: null;

		var connectionString = database switch
		{
			ConformanceDatabase.Sqlite => $"Data Source={sqlitePath}",
			ConformanceDatabase.PostgreSql when container is { } => container.GetConnectionString(),
			ConformanceDatabase.SqlServer when container is { } => container.GetConnectionString(),
			_ => throw new ArgumentOutOfRangeException(nameof(database)),
		};

		var contextOptions = database switch
		{
			ConformanceDatabase.Sqlite =>
				new DbContextOptionsBuilder<ConformanceDbContext>().UseSqlite(connectionString),

			ConformanceDatabase.PostgreSql =>
				new DbContextOptionsBuilder<ConformanceDbContext>().UseNpgsql(connectionString),

			ConformanceDatabase.SqlServer =>
				new DbContextOptionsBuilder<ConformanceDbContext>().UseSqlServer(connectionString),

			_ => throw new ArgumentOutOfRangeException(nameof(database)),
		};

		contextOptions.ReplaceService<IModelCacheKeyFactory, ConformanceSchemaModelCacheKeyFactory>();
		var contextFactory = new ConformanceDbContextFactory(contextOptions.Options, schema);

		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero));
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<TimeProvider>(clock);
		services.AddSingleton(clock);

		services.AddSingleton<IDbContextFactory<ConformanceDbContext>>(contextFactory);

		services.AddImmediateJobsCore()
			.ConfigureStorage(options =>
			{
				options.UseEntityFrameworkCore<ConformanceDbContext>();
				if (useDistributedTopology)
					_ = options.UseDistributed();
			});

		var servicesProvider = services.BuildServiceProvider(
			new ServiceProviderOptions
			{
				ValidateOnBuild = true,
				ValidateScopes = true,
			}
		);

		var fixture = new RelationalConformanceFixture(database, servicesProvider, connectionString, schema, sqlitePath);

		try
		{
			await using var context = contextFactory.CreateDbContext();
			var script = context.Database.GenerateCreateScript();

			foreach (var batch in Regex.Split(
				script,
				@"^\s*GO\s*$",
				RegexOptions.Multiline | RegexOptions.IgnoreCase,
				TimeSpan.FromSeconds(1)
			))
			{
				if (!string.IsNullOrWhiteSpace(batch))
					await context.Database.ExecuteSqlRawAsync(batch, TestContext.Current.CancellationToken);
			}

			return fixture;
		}
		catch
		{
			await fixture.DisposeAsync();
			throw;
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (services is not null)
			await services.DisposeAsync();

		if (sqlitePath is not null)
		{
			SqliteConnection.ClearAllPools();
			File.Delete(sqlitePath);
			return;
		}

		var cleanupOptions = database == ConformanceDatabase.PostgreSql
			? new DataOptions().UsePostgreSQL(connectionString)
			: new DataOptions().UseSqlServer(connectionString);

		await using var connection = new DataConnection(cleanupOptions);

		if (database == ConformanceDatabase.PostgreSql)
		{
			await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
			return;
		}

		foreach (var table in SqlServerTables)
			await connection.ExecuteAsync($"DROP TABLE IF EXISTS [{schema}].[{table}]");

		await connection.ExecuteAsync(
			$"IF SCHEMA_ID(N'{schema}') IS NOT NULL EXEC(N'DROP SCHEMA [{schema}]')"
		);
	}
}

file sealed class ConformanceDbContextFactory(
	DbContextOptions<ConformanceDbContext> options,
	string? schema
) : IDbContextFactory<ConformanceDbContext>
{
	public ConformanceDbContext CreateDbContext() => new(options, schema);
}

file sealed class ConformanceDbContext(
	DbContextOptions<ConformanceDbContext> options,
	string? schema
) : DbContext(options)
{
	internal string? Schema { get; } = schema;
	protected override void OnModelCreating(ModelBuilder modelBuilder) =>
		_ = modelBuilder.AddImmediateJobs(Schema);
}

[SuppressMessage("Performance", "CA1812", Justification = "Used via attribute")]
file sealed class ConformanceSchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
	public object Create(DbContext context, bool designTime) =>
		(context.GetType(), ((ConformanceDbContext)context).Schema, designTime);
}

[CollectionDefinition(Name)]
public sealed class EntityFrameworkCorePgSQLFixtureGroup : ICollectionFixture<EntityFrameworkCorePgSQLContainer>
{
	public const string Name = "EntityFrameworkCore-PgSQL";
}

public sealed class EntityFrameworkCorePgSQLContainer : IAsyncLifetime
{
	public PostgreSqlContainer PostgreSql { get; } = new PostgreSqlBuilder("postgres:18-alpine").Build();

	public async ValueTask InitializeAsync()
	{
		await PostgreSql.StartAsync();
	}

	public async ValueTask DisposeAsync()
	{
		await PostgreSql.DisposeAsync();
	}
}

[CollectionDefinition(Name)]
public sealed class EntityFrameworkCoreMsSQLFixtureGroup : ICollectionFixture<EntityFrameworkCoreMsSQLContainer>
{
	public const string Name = "EntityFrameworkCore-MsSQL";
}

public sealed class EntityFrameworkCoreMsSQLContainer : IAsyncLifetime
{
	public MsSqlContainer SqlServer { get; } = new MsSqlBuilder(
		"mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04"
	).Build();

	public async ValueTask InitializeAsync()
	{
		await SqlServer.StartAsync();
	}

	public async ValueTask DisposeAsync()
	{
		await SqlServer.DisposeAsync();
	}
}

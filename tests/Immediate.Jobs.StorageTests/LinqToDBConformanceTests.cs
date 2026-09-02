using System.Diagnostics.CodeAnalysis;
using DotNet.Testcontainers.Containers;
using Immediate.Jobs.LinqToDB;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Immediate.Jobs.StorageTests;

public static class LinqToDBConformanceTestCases
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

[Collection(LinqToDBPgSQLFixtureGroup.Name)]
public sealed class LinqToDBPgSQLConformanceTests(LinqToDBPgSQLContainer container)
{
	[Theory]
	[MemberData(nameof(LinqToDBConformanceTestCases.CreateCases), MemberType = typeof(LinqToDBConformanceTestCases))]
	public async Task LinqToDBConforms(
		ConformanceTopology topology,
		JobStorageConformanceTestCase testCase
	)
	{
		await using var fixture = await RelationalConformanceFixture.CreateAsync(
			ConformanceDatabase.PostgreSql,
			useDistributedTopology: topology == ConformanceTopology.Distributed,
			container: container.PostgreSql,
			testCase.PersistedJobState
		);

		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}
}

[Collection(LinqToDBMsSQLFixtureGroup.Name)]
public sealed class LinqToDBMsSQLConformanceTests(LinqToDBMsSQLContainer container)
{
	[Theory]
	[MemberData(nameof(LinqToDBConformanceTestCases.CreateCases), MemberType = typeof(LinqToDBConformanceTestCases))]
	public async Task LinqToDBConforms(
		ConformanceTopology topology,
		JobStorageConformanceTestCase testCase
	)
	{
		await using var fixture = await RelationalConformanceFixture.CreateAsync(
			ConformanceDatabase.SqlServer,
			useDistributedTopology: topology == ConformanceTopology.Distributed,
			container: container.SqlServer,
			testCase.PersistedJobState
		);

		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}
}

[Collection("LinqToDB-SQLite")]
public sealed class LinqToDBSQLiteConformanceTests
{
	[Theory]
	[MemberData(nameof(LinqToDBConformanceTestCases.CreateCases), MemberType = typeof(LinqToDBConformanceTestCases))]
	public async Task LinqToDBConforms(
		ConformanceTopology topology,
		JobStorageConformanceTestCase testCase
	)
	{
		await using var fixture = await RelationalConformanceFixture.CreateAsync(
			ConformanceDatabase.Sqlite,
			useDistributedTopology: topology == ConformanceTopology.Distributed,
			container: null,
			testCase.PersistedJobState
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
		IDatabaseContainer? container,
		PersistedJobState persistedJobState
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

		var dataOptions = database switch
		{
			ConformanceDatabase.Sqlite => new DataOptions().UseSQLite(connectionString),
			ConformanceDatabase.PostgreSql => new DataOptions().UsePostgreSQL(connectionString),
			ConformanceDatabase.SqlServer => new DataOptions().UseSqlServer(connectionString),
			_ => throw new ArgumentOutOfRangeException(nameof(database)),
		};

		var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero));
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<TimeProvider>(clock);
		services.AddSingleton(clock);

		services.AddLinqToDBContext<ConformanceDbContext>(() => dataOptions);

		services.AddImmediateJobsCore()
			.ConfigureStorage(options =>
			{
				options.UseLinqToDB<ConformanceDbContext>(schema);
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
			await using (var context = new ConformanceDbContext(dataOptions))
				await context.CreateImmediateJobsSchemaAsync(schema, TestContext.Current.CancellationToken);

			var storage = servicesProvider.GetRequiredService<LinqToDBJobStorage<ConformanceDbContext>>();
			await storage.LoadPersistedJobState(
				persistedJobState.Jobs,
				persistedJobState.Batches,
				persistedJobState.Edges,
				persistedJobState.RecurringSchedules
			);

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

[SuppressMessage("Performance", "CA1812", Justification = "Used via attribute")]
file sealed class ConformanceDbContext(
	DataOptions dataOptions
) : DataConnection(dataOptions);

[CollectionDefinition(Name)]
public sealed class LinqToDBPgSQLFixtureGroup : ICollectionFixture<LinqToDBPgSQLContainer>
{
	public const string Name = "LinqToDB-PgSQL";
}

public sealed class LinqToDBPgSQLContainer : IAsyncLifetime
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
public sealed class LinqToDBMsSQLFixtureGroup : ICollectionFixture<LinqToDBMsSQLContainer>
{
	public const string Name = "LinqToDB-MsSQL";
}

public sealed class LinqToDBMsSQLContainer : IAsyncLifetime
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

using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.LinqToDB;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

[Collection(StorageContainerFixtureGroup.Name)]
public sealed class LinqToDBConformanceTests(StorageContainers containers)
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring |
		StorageCapabilities.Graph |
		StorageCapabilities.FairQueues |
		StorageCapabilities.Replica;

	public static TheoryData<ConformanceDatabase, ConformanceTopology, JobStorageConformanceTestCase> Cases =>
		CreateCases();

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task LinqToDBConforms(
		ConformanceDatabase database,
		ConformanceTopology topology,
		JobStorageConformanceTestCase testCase
	)
	{
		ArgumentNullException.ThrowIfNull(testCase);
		await using var fixture = await RelationalConformanceFixture.CreateAsync(
			containers,
			database,
			TestContext.Current.CancellationToken,
			useDistributedTopology: topology == ConformanceTopology.Distributed
		);
		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}

	private static TheoryData<ConformanceDatabase, ConformanceTopology, JobStorageConformanceTestCase> CreateCases()
	{
		var data = new TheoryData<ConformanceDatabase, ConformanceTopology, JobStorageConformanceTestCase>();
		foreach (var database in Enum.GetValues<ConformanceDatabase>())
		{
			foreach (var topology in Enum.GetValues<ConformanceTopology>())
			{
				var capabilities = topology == ConformanceTopology.Distributed
					? Capabilities
					: Capabilities & ~StorageCapabilities.Replica;
				foreach (var testCase in JobStorageConformanceSuite.GetCases(capabilities))
					data.Add(database, topology, testCase);
			}
		}

		return data;
	}
}

file sealed class RelationalConformanceFixture : IAsyncDisposable
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

	private readonly ConformanceDatabase _database;
	private readonly string _connectionString;
	private readonly string? _schema;
	private readonly string? _sqlitePath;
	private readonly ServiceProvider? _services;

	private RelationalConformanceFixture(
		ConformanceDatabase database,
		ServiceProvider servicesProvider,
		string connectionString,
		string? schema,
		string? sqlitePath
	)
	{
		_database = database;
		_services = servicesProvider;
		_connectionString = connectionString;
		_schema = schema;
		_sqlitePath = sqlitePath;
	}

	internal IServiceProvider Services => _services
		?? throw new InvalidOperationException("The relational conformance fixture has not finished initializing.");

	internal static async ValueTask<RelationalConformanceFixture> CreateAsync(
		StorageContainers? containers,
		ConformanceDatabase database,
		CancellationToken cancellationToken,
		bool useDistributedTopology = true
	)
	{
		var schema = database == ConformanceDatabase.Sqlite ? null : "jobs_" + Guid.NewGuid().ToString("N");
		var sqlitePath = database == ConformanceDatabase.Sqlite
			? Path.Combine(Path.GetTempPath(), $"immediate-jobs-conformance-{Guid.NewGuid():N}.db")
			: null;

		var connectionString = database switch
		{
			ConformanceDatabase.Sqlite => $"Data Source={sqlitePath}",
			ConformanceDatabase.PostgreSql => GetContainers(containers).PostgreSql.GetConnectionString(),
			ConformanceDatabase.SqlServer => GetContainers(containers).SqlServer.GetConnectionString(),
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
				await context.CreateImmediateJobsSchemaAsync(schema, cancellationToken).ConfigureAwait(false);

			return fixture;
		}
		catch
		{
			await fixture.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private static StorageContainers GetContainers(StorageContainers? containers) =>
		containers ?? throw new InvalidOperationException("A container fixture is required for server databases.");

	public async ValueTask DisposeAsync()
	{
		if (_services is not null)
			await _services.DisposeAsync().ConfigureAwait(false);

		if (_sqlitePath is not null)
		{
			SqliteConnection.ClearAllPools();
			File.Delete(_sqlitePath);
			return;
		}

		var cleanupOptions = _database == ConformanceDatabase.PostgreSql
			? new DataOptions().UsePostgreSQL(_connectionString)
			: new DataOptions().UseSqlServer(_connectionString);

		await using var connection = new DataConnection(cleanupOptions);

		if (_database == ConformanceDatabase.PostgreSql)
		{
			_ = await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE").ConfigureAwait(false);
			return;
		}

		foreach (var table in SqlServerTables)
			_ = await connection.ExecuteAsync($"DROP TABLE IF EXISTS [{_schema}].[{table}]").ConfigureAwait(false);

		_ = await connection.ExecuteAsync(
			$"IF SCHEMA_ID(N'{_schema}') IS NOT NULL EXEC(N'DROP SCHEMA [{_schema}]')"
		).ConfigureAwait(false);
	}
}

[SuppressMessage("Performance", "CA1812", Justification = "Used via attribute")]
file sealed class ConformanceDbContext(
	DataOptions dataOptions
) : DataConnection(dataOptions);

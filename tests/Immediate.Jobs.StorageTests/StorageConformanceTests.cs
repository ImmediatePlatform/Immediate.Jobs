using System.Text.RegularExpressions;
using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.LinqToDB;
using Immediate.Jobs.Redis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace Immediate.Jobs.StorageTests;

[Collection(StorageContainerFixtureGroup.Name)]
public sealed class RelationalStorageConformanceTests(StorageContainers containers)
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring |
		StorageCapabilities.Graph |
		StorageCapabilities.FairQueues |
		StorageCapabilities.Replica;

	public static TheoryData<ConformanceDatabase, ConformanceAdapter, ConformanceTopology, JobStorageConformanceTestCase> Cases
	{
		get
		{
			var data = new TheoryData<ConformanceDatabase, ConformanceAdapter, ConformanceTopology, JobStorageConformanceTestCase>();
			foreach (var database in Enum.GetValues<ConformanceDatabase>())
			{
				foreach (var adapter in Enum.GetValues<ConformanceAdapter>())
				{
					foreach (var topology in Enum.GetValues<ConformanceTopology>())
					{
						var capabilities = topology == ConformanceTopology.Distributed
							? Capabilities
							: Capabilities & ~StorageCapabilities.Replica;
						foreach (var testCase in JobStorageConformanceSuite.GetCases(capabilities))
							data.Add(database, adapter, topology, testCase);
					}
				}
			}

			return data;
		}
	}

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task RelationalStorageConforms(
		ConformanceDatabase database,
		ConformanceAdapter adapter,
		ConformanceTopology topology,
		JobStorageConformanceTestCase testCase
	)
	{
		ArgumentNullException.ThrowIfNull(testCase);
		await using var fixture = await RelationalConformanceFixture.CreateAsync(
			containers,
			database,
			adapter,
			TestContext.Current.CancellationToken,
			useDistributedTopology: topology == ConformanceTopology.Distributed
		);
		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}
}

[Collection(RedisContainerFixtureGroup.Name)]
public sealed class RedisStorageConformanceTests(RedisStorageFixture redis)
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring;

	public static TheoryData<JobStorageConformanceTestCase> Cases =>
		[.. JobStorageConformanceSuite.GetCases(Capabilities)];

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task RedisStorageConforms(JobStorageConformanceTestCase testCase)
	{
		ArgumentNullException.ThrowIfNull(testCase);
		await using var fixture = await RedisConformanceFixture.CreateAsync(
			redis.Container.GetConnectionString(),
			TestContext.Current.CancellationToken
		);
		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}
}

public enum ConformanceDatabase
{
	Sqlite,
	PostgreSql,
	SqlServer,
}

public enum ConformanceAdapter
{
	EntityFrameworkCore,
	LinqToDB,
}

public enum ConformanceTopology
{
	Distributed,
	SingleServer,
}

internal sealed class RelationalConformanceFixture : IAsyncDisposable
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
	private ServiceProvider? _services;

	private RelationalConformanceFixture(
		ConformanceDatabase database,
		string connectionString,
		string? schema,
		string? sqlitePath
	)
	{
		_database = database;
		_connectionString = connectionString;
		_schema = schema;
		_sqlitePath = sqlitePath;
	}

	internal IServiceProvider Services => _services
		?? throw new InvalidOperationException("The relational conformance fixture has not finished initializing.");

	internal static async ValueTask<RelationalConformanceFixture> CreateAsync(
		StorageContainers? containers,
		ConformanceDatabase database,
		ConformanceAdapter adapter,
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
		var contextOptions = new DbContextOptionsBuilder<ConformanceDbContext>();
		DataOptions dataOptions;
		switch (database)
		{
			case ConformanceDatabase.Sqlite:
				dataOptions = new DataOptions().UseSQLite(connectionString);
				_ = contextOptions.UseSqlite(connectionString);
				break;
			case ConformanceDatabase.PostgreSql:
				dataOptions = new DataOptions().UsePostgreSQL(connectionString);
				_ = contextOptions.UseNpgsql(connectionString);
				break;
			case ConformanceDatabase.SqlServer:
				dataOptions = new DataOptions().UseSqlServer(connectionString);
				_ = contextOptions.UseSqlServer(connectionString);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(database));
		}

		_ = contextOptions.ReplaceService<IModelCacheKeyFactory, ConformanceSchemaModelCacheKeyFactory>();
		var contextFactory = new ConformanceDbContextFactory(contextOptions.Options, schema);
		var fixture = new RelationalConformanceFixture(database, connectionString, schema, sqlitePath);
		try
		{
			if (adapter == ConformanceAdapter.LinqToDB)
			{
				await dataOptions.CreateImmediateJobsSchemaAsync(schema, cancellationToken).ConfigureAwait(false);
			}
			else
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
						_ = await context.Database.ExecuteSqlRawAsync(batch, cancellationToken).ConfigureAwait(false);
				}
			}

			var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero));
			var serviceCollection = new ServiceCollection();
			_ = serviceCollection.AddLogging();
			_ = serviceCollection.AddSingleton<TimeProvider>(clock);
			_ = serviceCollection.AddSingleton(clock);
			_ = serviceCollection.AddSingleton<IDbContextFactory<ConformanceDbContext>>(contextFactory);
			_ = serviceCollection.AddImmediateJobsCore()
				.ConfigureStorage(options =>
				{
					_ = adapter == ConformanceAdapter.EntityFrameworkCore
						? options.UseEntityFrameworkCore<ConformanceDbContext>()
						: options.UseLinqToDB(dataOptions, schema);
					if (useDistributedTopology)
						_ = options.UseDistributed();
				});
			fixture._services = serviceCollection.BuildServiceProvider(new ServiceProviderOptions
			{
				ValidateOnBuild = true,
				ValidateScopes = true,
			});
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

internal sealed class RedisConformanceFixture : IAsyncDisposable
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
			_ = serviceCollection.AddLogging();
			_ = serviceCollection.AddSingleton<TimeProvider>(clock);
			_ = serviceCollection.AddSingleton(clock);
			_ = serviceCollection.AddImmediateJobsCore()
				.ConfigureStorage(options =>
					_ = options.UseRedis(connection, storage => storage.KeyPrefix = keyPrefix)
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

internal sealed class ConformanceDbContextFactory(
	DbContextOptions<ConformanceDbContext> options,
	string? schema
) : IDbContextFactory<ConformanceDbContext>
{
	public ConformanceDbContext CreateDbContext() => new(options, schema);
}

internal sealed class ConformanceDbContext(
	DbContextOptions<ConformanceDbContext> options,
	string? schema
) : DbContext(options)
{
	internal string? Schema { get; } = schema;

	protected override void OnModelCreating(ModelBuilder modelBuilder) =>
		_ = modelBuilder.AddImmediateJobs(Schema);
}

internal sealed class ConformanceSchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
	public object Create(DbContext context, bool designTime) =>
		(context.GetType(), ((ConformanceDbContext)context).Schema, designTime);
}

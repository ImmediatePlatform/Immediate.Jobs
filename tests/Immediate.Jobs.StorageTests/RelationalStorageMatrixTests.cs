using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.LinqToDB;
using global::LinqToDB;
using global::LinqToDB.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using System.Text.RegularExpressions;

namespace Immediate.Jobs.StorageTests;

#pragma warning disable CS1591
[Collection(StorageContainerFixtureGroup.Name)]
public sealed class RelationalStorageMatrixTests(StorageContainers containers)
{
	public static TheoryData<DatabaseKind, AdapterKind> Matrix => new()
	{
		{ DatabaseKind.Sqlite, AdapterKind.EntityFrameworkCore },
		{ DatabaseKind.Sqlite, AdapterKind.LinqToDB },
		{ DatabaseKind.PostgreSql, AdapterKind.EntityFrameworkCore },
		{ DatabaseKind.PostgreSql, AdapterKind.LinqToDB },
		{ DatabaseKind.SqlServer, AdapterKind.EntityFrameworkCore },
		{ DatabaseKind.SqlServer, AdapterKind.LinqToDB },
	};

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterPassesCoreStorageConformance(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.Storage;
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob("parent", now) with { Context = "{\"tenant\":\"matrix\"}", BatchId = "batch" };
		var child = CreateJob("child", now) with
		{
			BatchId = "batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(new()
		{
			Id = "batch",
			CreatedAt = now,
			TotalJobs = 2,
			PendingCount = 2,
			State = BatchState.Executing,
		}, [parent, child], [new()
		{
			ChildJobId = child.Id,
			ParentJobId = parent.Id,
			Trigger = ContinuationTrigger.Success,
		}], cancellationToken);

		var acquiredParent = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		Assert.Equal(parent.Id, acquiredParent.Id);
		Assert.Equal(parent.Context, acquiredParent.Context);
		var executionStartedAt = now.AddSeconds(1);
		await storage.SetExecutionTelemetryAsync(
			parent.Id,
			"worker",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			executionStartedAt,
			cancellationToken
		);
		var correlated = Assert.Single(await storage.QueryJobsAsync(new() { Id = parent.Id }, cancellationToken));
		Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", correlated.ExecutionTraceId);
		Assert.Equal("00f067aa0ba902b7", correlated.ExecutionSpanId);
		Assert.Equal(executionStartedAt, correlated.ExecutionStartedAt);
		await storage.CompleteAsync(parent.Id, "worker", cancellationToken);
		var acquiredChild = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		Assert.Equal(child.Id, acquiredChild.Id);
		await storage.CompleteAsync(child.Id, "worker", cancellationToken);
		var status = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(BatchState.Succeeded, status.State);
		Assert.Equal(2, status.Succeeded);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterHandlesContentionAndLeaseRecovery(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		foreach (var index in Enumerable.Range(0, 24))
			await first.EnqueueAsync(CreateJob($"contended-{index}", now), cancellationToken);

		var claims = await Task.WhenAll(
			first.AcquireDueJobsAsync(CreateRequest("node-a", 24), cancellationToken).AsTask(),
			second.AcquireDueJobsAsync(CreateRequest("node-b", 24), cancellationToken).AsTask()
		);
		var claimed = claims.SelectMany(static claim => claim).ToArray();
		Assert.Equal(24, claimed.Select(job => job.Id).Distinct().Count());
		foreach (var job in claimed)
			await first.CompleteAsync(job.Id, job.WorkerId!, cancellationToken);

		var leased = CreateJob("leased", now.AddMinutes(1));
		await first.EnqueueAsync(leased, cancellationToken);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
		_ = Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		await first.SetExecutionTelemetryAsync(
			leased.Id,
			"node-a",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			fixture.TimeProvider.GetUtcNow(),
			cancellationToken
		);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
		var recovered = Assert.Single(await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));
		Assert.Equal("leased", recovered.Id);
		Assert.Equal(2, recovered.Attempt);
		Assert.Null(recovered.ExecutionTraceId);
		Assert.Null(recovered.ExecutionSpanId);
		Assert.Null(recovered.ExecutionStartedAt);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterDeduplicatesRecurringAndRollsBackInvalidGraphs(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "recurring",
			JobName = "matrix-test",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		var occurrence = CreateJob("occurrence", now) with { RecurringKey = $"recurring:{now.UtcTicks}" };
		await storage.UpsertRecurringAsync(schedule, cancellationToken);
		Assert.True(await storage.MaterializeRecurringAsync(schedule, occurrence, now.AddHours(1), cancellationToken));
		Assert.False(await storage.MaterializeRecurringAsync(schedule, occurrence, now.AddHours(1), cancellationToken));
		Assert.Equal("recurring", Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring).Name);

		var invalid = CreateJob("invalid-child", now) with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		_ = await Assert.ThrowsAnyAsync<Exception>(() => storage.EnqueueContinuationAsync(
			invalid,
			[new() { ChildJobId = invalid.Id, ParentJobId = "missing" }],
			cancellationToken
		).AsTask());
		Assert.Null(await storage.GetJobStatusAsync(invalid.Id, cancellationToken));
	}

	[Theory]
	[InlineData(DatabaseKind.Sqlite)]
	[InlineData(DatabaseKind.PostgreSql)]
	[InlineData(DatabaseKind.SqlServer)]
	public async Task AdaptersShareLinqToDBCreatedSchema(DatabaseKind database)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, AdapterKind.LinqToDB, cancellationToken);
		var efStorage = fixture.CreateEntityFrameworkCoreStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await fixture.Storage.EnqueueAsync(CreateJob("from-linq2db", now), cancellationToken);
		Assert.Equal("from-linq2db", Assert.Single(await efStorage.QueryJobsAsync(
			new() { Id = "from-linq2db" },
			cancellationToken
		)).Id);
		await efStorage.EnqueueAsync(CreateJob("from-ef", now.AddTicks(1)), cancellationToken);
		Assert.Equal("from-ef", Assert.Single(await fixture.Storage.QueryJobsAsync(
			new() { Id = "from-ef" },
			cancellationToken
		)).Id);
	}

	[Theory]
	[InlineData(DatabaseKind.Sqlite)]
	[InlineData(DatabaseKind.PostgreSql)]
	[InlineData(DatabaseKind.SqlServer)]
	public async Task AdaptersShareEntityFrameworkCoreCreatedSchema(DatabaseKind database)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, AdapterKind.EntityFrameworkCore, cancellationToken);
		var linqStorage = fixture.CreateLinqToDBStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await fixture.Storage.EnqueueAsync(CreateJob("from-ef", now), cancellationToken);
		Assert.Equal("from-ef", Assert.Single(await linqStorage.QueryJobsAsync(
			new() { Id = "from-ef" },
			cancellationToken
		)).Id);
		await linqStorage.EnqueueAsync(CreateJob("from-linq2db", now.AddTicks(1)), cancellationToken);
		Assert.Equal("from-linq2db", Assert.Single(await fixture.Storage.QueryJobsAsync(
			new() { Id = "from-linq2db" },
			cancellationToken
		)).Id);
	}

	private async Task<MatrixFixture> CreateFixtureAsync(
		DatabaseKind database,
		AdapterKind adapter,
		CancellationToken cancellationToken
	)
	{
		var schema = database == DatabaseKind.Sqlite ? null : "jobs_" + Guid.NewGuid().ToString("N");
		var sqlitePath = database == DatabaseKind.Sqlite
			? Path.Combine(Path.GetTempPath(), $"immediate-jobs-matrix-{Guid.NewGuid():N}.db")
			: null;
		var connectionString = database switch
		{
			DatabaseKind.Sqlite => $"Data Source={sqlitePath}",
			DatabaseKind.PostgreSql => containers.PostgreSql.GetConnectionString(),
			DatabaseKind.SqlServer => containers.SqlServer.GetConnectionString(),
			_ => throw new ArgumentOutOfRangeException(nameof(database)),
		};
		var contextOptions = new DbContextOptionsBuilder<MatrixDbContext>();
		DataOptions dataOptions;
		if (database == DatabaseKind.Sqlite)
		{
			dataOptions = new DataOptions().UseSQLite(connectionString);
			_ = contextOptions.UseSqlite(connectionString);
		}
		else if (database == DatabaseKind.PostgreSql)
		{
			dataOptions = new DataOptions().UsePostgreSQL(connectionString);
			_ = contextOptions.UseNpgsql(connectionString);
		}
		else
		{
			dataOptions = new DataOptions().UseSqlServer(connectionString);
			_ = contextOptions.UseSqlServer(connectionString);
		}

		_ = contextOptions.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();
		var contextFactory = new MatrixDbContextFactory(contextOptions.Options, schema);
		if (adapter == AdapterKind.LinqToDB)
		{
			await dataOptions.CreateImmediateJobsSchemaAsync(schema, cancellationToken);
			await dataOptions.CreateImmediateJobsSchemaAsync(schema, cancellationToken);
		}
		else
		{
			await using var context = contextFactory.CreateDbContext();
			var script = context.Database.GenerateCreateScript();
			foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
			{
				if (!string.IsNullOrWhiteSpace(batch))
					_ = await context.Database.ExecuteSqlRawAsync(batch, cancellationToken);
			}
		}

		return new(database, connectionString, schema, sqlitePath, dataOptions, contextFactory, adapter);
	}

	private static JobAcquisitionRequest CreateRequest(string workerId, int batchSize) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = batchSize,
		Queues = [new()
		{
			QueueName = JobQueueDefinition.DefaultName,
			Capacity = batchSize,
			JobCapacities = new Dictionary<string, int> { ["matrix-test"] = batchSize },
		}],
	};

	private static JobRecord CreateJob(string id, DateTimeOffset now) => new()
	{
		Id = id,
		JobName = "matrix-test",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now,
	};

	public enum DatabaseKind
	{
		Sqlite,
		PostgreSql,
		SqlServer,
	}

	public enum AdapterKind
	{
		EntityFrameworkCore,
		LinqToDB,
	}

	private sealed class MatrixFixture(
		DatabaseKind database,
		string connectionString,
		string? schema,
		string? sqlitePath,
		DataOptions dataOptions,
		MatrixDbContextFactory contextFactory,
		AdapterKind adapter
	) : IAsyncDisposable
	{
		public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
		public IJobStorage Storage => adapter == AdapterKind.LinqToDB
			? new LinqToDBJobStorage(dataOptions, schema, TimeProvider)
			: CreateEntityFrameworkCoreStorage();

		public IJobStorage CreateStorage() => Storage;

		public EntityFrameworkCoreJobStorage<MatrixDbContext> CreateEntityFrameworkCoreStorage() =>
			new(contextFactory, TimeProvider);

		public LinqToDBJobStorage CreateLinqToDBStorage() => new(dataOptions, schema, TimeProvider);

		public async ValueTask DisposeAsync()
		{
			if (sqlitePath is not null)
			{
				File.Delete(sqlitePath);
				return;
			}

			var cleanupOptions = database == DatabaseKind.PostgreSql
				? new DataOptions().UsePostgreSQL(connectionString)
				: new DataOptions().UseSqlServer(connectionString);
			await using var connection = new global::LinqToDB.Data.DataConnection(cleanupOptions);
			if (database == DatabaseKind.PostgreSql)
			{
				_ = await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
			}
			else
			{
				foreach (var table in new[]
				{
					"immediate_job_continuations",
					"immediate_jobs",
					"immediate_job_batches",
					"immediate_recurring_jobs",
					"immediate_job_servers",
				})
				{
					_ = await connection.ExecuteAsync($"DROP TABLE IF EXISTS [{schema}].[{table}]");
				}

				_ = await connection.ExecuteAsync(
					$"IF SCHEMA_ID(N'{schema}') IS NOT NULL EXEC(N'DROP SCHEMA [{schema}]')"
				);
			}
		}
	}

	private sealed class MatrixDbContextFactory(
		DbContextOptions<MatrixDbContext> options,
		string? schema
	) : IDbContextFactory<MatrixDbContext>
	{
		public MatrixDbContext CreateDbContext() => new(options, schema);
	}

	private sealed class MatrixDbContext(DbContextOptions<MatrixDbContext> options, string? schema)
		: DbContext(options)
	{
		public string? Schema { get; } = schema;

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			_ = modelBuilder.AddImmediateJobs(Schema);
		}
	}

	private sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
	{
		public object Create(DbContext context, bool designTime) =>
			(context.GetType(), ((MatrixDbContext)context).Schema, designTime);
	}
}
#pragma warning restore CS1591

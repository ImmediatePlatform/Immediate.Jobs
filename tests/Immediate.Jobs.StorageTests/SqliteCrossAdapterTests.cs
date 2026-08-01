using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.LinqToDB;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

#pragma warning disable CS1591
public sealed class SqliteCrossAdapterTests
{
	[Fact]
	public async Task LinqToDBReadsAndWritesEntityFrameworkCoreSchema()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CrossAdapterFixture.CreateAsync(createWithEntityFrameworkCore: true, cancellationToken);
		await VerifyBothDirectionsAsync(fixture, cancellationToken);
	}

	[Fact]
	public async Task EntityFrameworkCoreReadsAndWritesLinqToDBSchema()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CrossAdapterFixture.CreateAsync(createWithEntityFrameworkCore: false, cancellationToken);
		await VerifyBothDirectionsAsync(fixture, cancellationToken);
	}

	private static async Task VerifyBothDirectionsAsync(
		CrossAdapterFixture fixture,
		CancellationToken cancellationToken
	)
	{
		var now = fixture.TimeProvider.GetUtcNow();
		var efJob = CreateJob("from-ef", now);
		await fixture.EntityFrameworkCore.EnqueueAsync(efJob, cancellationToken);
		Assert.Equal(efJob.Id, Assert.Single(await fixture.LinqToDB.QueryJobsAsync(
			new() { Id = efJob.Id },
			cancellationToken
		)).Id);

		var linqJob = CreateJob("from-linq2db", now.AddTicks(1));
		await fixture.LinqToDB.EnqueueAsync(linqJob, cancellationToken);
		Assert.Equal(linqJob.Id, Assert.Single(await fixture.EntityFrameworkCore.QueryJobsAsync(
			new() { Id = linqJob.Id },
			cancellationToken
		)).Id);
	}

	private static JobRecord CreateJob(string id, DateTimeOffset now) => new()
	{
		Id = id,
		JobName = "cross-adapter",
		Payload = "{}",
		Context = "{\"tenant\":\"shared\"}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now,
	};

	private sealed class CrossAdapterFixture(
		string databasePath,
		TestDbContextFactory contextFactory,
		DataOptions options
	) : IAsyncDisposable
	{
		private readonly List<IJobStorage> _storages = [];

		public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero));
		public EntityFrameworkCoreJobStorage<TestDbContext> EntityFrameworkCore
		{
			get
			{
				var storage = new EntityFrameworkCoreJobStorage<TestDbContext>(contextFactory, TimeProvider);
				_storages.Add(storage);
				return storage;
			}
		}

		public LinqToDBJobStorage LinqToDB
		{
			get
			{
				var storage = new LinqToDBJobStorage(options, timeProvider: TimeProvider);
				_storages.Add(storage);
				return storage;
			}
		}

		public static async Task<CrossAdapterFixture> CreateAsync(
			bool createWithEntityFrameworkCore,
			CancellationToken cancellationToken
		)
		{
			var databasePath = Path.Combine(Path.GetTempPath(), $"immediate-jobs-cross-{Guid.NewGuid():N}.db");
			var connectionString = $"Data Source={databasePath}";
			var contextOptions = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connectionString).Options;
			var contextFactory = new TestDbContextFactory(contextOptions);
			var dataOptions = new DataOptions().UseSQLite(connectionString);
			var fixture = new CrossAdapterFixture(databasePath, contextFactory, dataOptions);
			try
			{
				if (createWithEntityFrameworkCore)
				{
					await using var context = contextFactory.CreateDbContext();
					_ = await context.Database.EnsureCreatedAsync(cancellationToken);
				}
				else
				{
					await dataOptions.CreateImmediateJobsSchemaAsync(cancellationToken: cancellationToken);
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
			foreach (var storage in _storages.AsEnumerable().Reverse())
				await storage.DisposeAsync();
			SqliteConnection.ClearAllPools();
			File.Delete(databasePath);
		}

	}

	private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
	{
		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			_ = modelBuilder.AddImmediateJobs();
		}
	}

	private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
		: IDbContextFactory<TestDbContext>
	{
		public TestDbContext CreateDbContext() => new(options);
	}
}
#pragma warning restore CS1591

using Immediate.Jobs.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class EntityFrameworkCoreJobStorageTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("{\"usage\":{\"userId\":\"42\"}}")]
	public async Task ContextRoundTripsThroughEntityFrameworkCoreStorage(string? context)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with { Context = context };

		await storage.EnqueueAsync(job, cancellationToken);

		var queried = Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken));
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("context-worker", 1), cancellationToken));
		Assert.Equal(context, queried.Context);
		Assert.Equal(context, acquired.Context);
	}

	[Fact]
	public async Task CompetingNodesClaimEachInvocationOnce()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();

		foreach (var index in Enumerable.Range(0, 64))
			await first.EnqueueAsync(CreateJob(now, index), cancellationToken);

		var firstClaim = first.AcquireDueJobsAsync(CreateRequest("node-a", 64), cancellationToken).AsTask();
		var secondClaim = second.AcquireDueJobsAsync(CreateRequest("node-b", 64), cancellationToken).AsTask();
		var claims = await Task.WhenAll(firstClaim, secondClaim);
		var claimed = claims.SelectMany(static claim => claim).ToArray();
		Assert.Equal(64, claimed.Length);
		Assert.Equal(64, claimed.Select(job => job.Id).Distinct().Count());
	}

	[Fact]
	public async Task ExpiredLeaseIsRecoveredByAnotherNode()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1);
		await first.EnqueueAsync(job, cancellationToken);

		_ = Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));

		var recovered = Assert.Single(
			await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken)
		);
		Assert.Equal(job.Id, recovered.Id);
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-b", recovered.WorkerId);
	}

	[Fact]
	public async Task SingleServerRestoresDurableEfJobsIntoMemory()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		using var firstProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1);
		await firstProcess.EnqueueAsync(job, cancellationToken);

		using var restartedProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		await restartedProcess.InitializeAsync(cancellationToken);

		Assert.Equal(job.Id, Assert.Single(await restartedProcess.QueryJobsAsync(new(), cancellationToken)).Id);
		Assert.Equal(
			job.Id,
			Assert.Single(await restartedProcess.AcquireDueJobsAsync(CreateRequest("restarted", 1), cancellationToken)).Id
		);
	}

	[Fact]
	public async Task RecurringMaterializationRunsInsideConfiguredExecutionStrategy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			useRetryingExecutionStrategy: true
		);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var nextRunAt = now.AddMinutes(1);
		var schedule = new RecurringJobSchedule
		{
			Name = "retrying-strategy",
			JobName = "ef-test",
			Cron = "0 * * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		var job = CreateJob(now, 1) with
		{
			RecurringKey = $"{schedule.Name}:{schedule.NextRunAt.UtcTicks}",
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);

		var materialized = await storage.MaterializeRecurringAsync(
			schedule,
			job,
			nextRunAt,
			cancellationToken
		);

		Assert.True(materialized);
		Assert.Equal(job.Id, Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken)).Id);
		var storedSchedule = Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
		Assert.Equal(now, storedSchedule.LastRunAt);
		Assert.Equal(nextRunAt, storedSchedule.NextRunAt);
	}

	[Fact]
	public async Task CodeDefinedScheduleCannotBeReplacedByDynamicScheduleInEntityFrameworkCore()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var codeDefined = new RecurringJobSchedule
		{
			Name = "cleanup",
			JobName = "cleanup",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			IsPaused = true,
			NextRunAt = fixture.TimeProvider.GetUtcNow() + TimeSpan.FromHours(1),
		};
		await storage.UpsertRecurringAsync(codeDefined, cancellationToken);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			storage.UpsertRecurringAsync(
				codeDefined with { Cron = "0 0 * * *", IsCodeDefined = false },
				cancellationToken
			).AsTask()
		);
		Assert.Equal("Code-defined recurring schedules cannot be replaced by dynamic schedules.", exception.Message);

		await storage.UpsertRecurringAsync(codeDefined with { Cron = "0 0 * * *", IsPaused = false }, cancellationToken);
		var stored = Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
		Assert.Equal("0 0 * * *", stored.Cron);
		Assert.True(stored.IsCodeDefined);
		Assert.True(stored.IsPaused);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ObsoleteCodeDefinedSchedulesAreRemovedFromEntityFrameworkCore(bool preserveCurrent)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var current = CreateSchedule("current", isCodeDefined: true, fixture.TimeProvider);
		var obsolete = CreateSchedule("obsolete", isCodeDefined: true, fixture.TimeProvider);
		var dynamic = CreateSchedule("dynamic", isCodeDefined: false, fixture.TimeProvider);
		await storage.UpsertRecurringAsync(current, cancellationToken);
		await storage.UpsertRecurringAsync(obsolete, cancellationToken);
		await storage.UpsertRecurringAsync(dynamic, cancellationToken);

		await storage.RemoveObsoleteCodeDefinedRecurringAsync(
			preserveCurrent ? [current.Name] : [],
			cancellationToken
		);

		var expectedNames = preserveCurrent ? ["current", "dynamic"] : new[] { "dynamic" };
		var names = (await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring
			.Select(static schedule => schedule.Name)
			.Order(StringComparer.Ordinal);
		Assert.Equal(expectedNames.Order(StringComparer.Ordinal), names);
	}

	private static JobAcquisitionRequest CreateRequest(string workerId, int batchSize) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = batchSize,
		Queues = [new() { QueueName = JobQueueDefinition.DefaultName, Capacity = batchSize, JobCapacities = new Dictionary<string, int> { ["ef-test"] = batchSize } }],
	};

	private static JobRecord CreateJob(DateTimeOffset now, int index) => new()
	{
		Id = "job-" + Guid.NewGuid().ToString("N"),
		JobName = "ef-test",
		Payload = $"{{\"index\":{index}}}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now.AddTicks(index),
	};

	private static RecurringJobSchedule CreateSchedule(
		string name,
		bool isCodeDefined,
		TimeProvider timeProvider
	) => new()
	{
		Name = name,
		JobName = "ef-test",
		Cron = "0 * * * *",
		TimeZone = "UTC",
		IsCodeDefined = isCodeDefined,
		NextRunAt = timeProvider.GetUtcNow() + TimeSpan.FromHours(1),
	};

	private sealed class StorageFixture(
		string connectionString,
		ServiceProvider services,
		IDbContextFactory<TestDbContext> contextFactory,
		FakeTimeProvider timeProvider
	) : IAsyncDisposable
	{
		private readonly SqliteConnection _anchor = new(connectionString);

		public FakeTimeProvider TimeProvider { get; } = timeProvider;

		public EntityFrameworkCoreJobStorage<TestDbContext> CreateStorage() => new(contextFactory, TimeProvider);

		public static async Task<StorageFixture> CreateAsync(
			CancellationToken cancellationToken,
			bool useRetryingExecutionStrategy = false
		)
		{
			var connectionString = $"Data Source=jobs-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
			var services = new ServiceCollection();
			_ = services.AddDbContextFactory<TestDbContext>(options =>
			{
				_ = options.UseSqlite(connectionString);
				if (useRetryingExecutionStrategy)
					_ = options.ReplaceService<IExecutionStrategyFactory, RetryingExecutionStrategyFactory>();
			});
			var provider = services.BuildServiceProvider();
			var factory = provider.GetRequiredService<IDbContextFactory<TestDbContext>>();
			var fixture = new StorageFixture(
				connectionString,
				provider,
				factory,
				new(new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero))
			);
			try
			{
				await fixture._anchor.OpenAsync(cancellationToken);
				await using var context = await factory.CreateDbContextAsync(cancellationToken);
				_ = await context.Database.EnsureCreatedAsync(cancellationToken);
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
			await services.DisposeAsync();
			await _anchor.DisposeAsync();
		}
	}

	private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
	{
		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			_ = modelBuilder.AddImmediateJobs();
		}
	}

	private sealed class RetryingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
		: IExecutionStrategyFactory
	{
		public IExecutionStrategy Create() => new RetryingExecutionStrategy(dependencies);
	}

	private sealed class RetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
		: ExecutionStrategy(dependencies, DefaultMaxRetryCount, DefaultMaxDelay)
	{
		protected override bool ShouldRetryOn(Exception exception) => false;
	}
}
#pragma warning restore CS1591

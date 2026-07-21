using Immediate.Jobs.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
		await Task.WhenAll(firstClaim, secondClaim);

		var claimed = firstClaim.Result.Concat(secondClaim.Result).ToArray();
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

		Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
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
		var firstProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1);
		await firstProcess.EnqueueAsync(job, cancellationToken);

		var restartedProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		await restartedProcess.InitializeAsync(cancellationToken);

		Assert.Equal(job.Id, Assert.Single(await restartedProcess.QueryJobsAsync(new(), cancellationToken)).Id);
		Assert.Equal(
			job.Id,
			Assert.Single(await restartedProcess.AcquireDueJobsAsync(CreateRequest("restarted", 1), cancellationToken)).Id
		);
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
		Id = Guid.NewGuid(),
		JobName = "ef-test",
		Payload = $"{{\"index\":{index}}}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now.AddTicks(index),
	};

	private sealed class StorageFixture(
		SqliteConnection anchor,
		ServiceProvider services,
		IDbContextFactory<TestDbContext> contextFactory,
		FakeTimeProvider timeProvider
	) : IAsyncDisposable
	{
		public FakeTimeProvider TimeProvider { get; } = timeProvider;

		public EntityFrameworkCoreJobStorage<TestDbContext> CreateStorage() => new(contextFactory, TimeProvider);

		public static async Task<StorageFixture> CreateAsync(CancellationToken cancellationToken)
		{
			var connectionString = $"Data Source=jobs-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
			var anchor = new SqliteConnection(connectionString);
			await anchor.OpenAsync(cancellationToken);

			var services = new ServiceCollection();
			services.AddDbContextFactory<TestDbContext>(options => options.UseSqlite(connectionString));
			var provider = services.BuildServiceProvider();
			var factory = provider.GetRequiredService<IDbContextFactory<TestDbContext>>();
			await using (var context = await factory.CreateDbContextAsync(cancellationToken))
				await context.Database.EnsureCreatedAsync(cancellationToken);

			return new(anchor, provider, factory, new(new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero)));
		}

		public async ValueTask DisposeAsync()
		{
			await services.DisposeAsync();
			await anchor.DisposeAsync();
		}
	}

	private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
	{
		protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddImmediateJobs();
	}
}
#pragma warning restore CS1591

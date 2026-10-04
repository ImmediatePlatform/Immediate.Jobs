using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class EntityFrameworkCoreAcquisitionTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AcquisitionPropagatesExecutionInsertFailure(bool useFairQueues)
	{
		var token = TestContext.Current.CancellationToken;
		await using var connection = new SqliteConnection("Data Source=:memory:");
		await connection.OpenAsync(token);
		var options = new DbContextOptionsBuilder<AcquisitionDbContext>().UseSqlite(connection).Options;
		var factory = new AcquisitionDbContextFactory(options);
		await using var context = factory.CreateDbContext();
		_ = await context.Database.EnsureCreatedAsync(token);
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new EntityFrameworkCoreJobStorage<AcquisitionDbContext>(factory, clock);
		var job = new JobRecord
		{
			JobHandle = JobHandle.FromString("execution-insert-failure"),
			JobName = "failed-acquisition",
			QueueName = "failed-acquisition",
			GroupId = "tenant",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = clock.GetUtcNow(),
			CreatedAt = clock.GetUtcNow(),
		};
		await storage.EnqueueAsync(job, token);
		_ = await context.Database.ExecuteSqlRawAsync(
			"CREATE TRIGGER reject_execution BEFORE INSERT ON immediate_job_executions BEGIN SELECT RAISE(ABORT, 'execution insert rejected'); END;",
			token);
		var request = new JobAcquisitionRequest
		{
			WorkerId = "worker",
			BatchSize = 1,
			Lease = TimeSpan.FromMinutes(1),
			FairQueues = useFairQueues ? new()
			{
				GroupRoundRobin = true,
				ConcurrencyShareThreshold = 0.5,
				MinInflightForNoisy = 1,
			} : null,
			Queues = [new() { QueueName = job.QueueName, Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [job.JobName] = 1 } }],
		};

		var exception = await Assert.ThrowsAsync<DbUpdateException>(() => storage.AcquireDueJobsAsync(request, token).AsTask());
		Assert.Contains("execution insert rejected", exception.InnerException!.Message, StringComparison.Ordinal);
		var persisted = await context.Set<ImmediateJobEntity>().AsNoTracking().SingleAsync(token);
		Assert.Equal(JobState.Pending, persisted.State);
		Assert.Equal(0, persisted.Attempt);
		Assert.Empty(await context.Set<ImmediateJobExecutionEntity>().AsNoTracking().ToListAsync(token));
		Assert.Empty(await context.Set<ImmediateFairQueueGroupEntity>().AsNoTracking().ToListAsync(token));
	}
}

file sealed class AcquisitionDbContext(DbContextOptions<AcquisitionDbContext> options) : DbContext(options)
{
	protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddImmediateJobs();
}

file sealed class AcquisitionDbContextFactory(DbContextOptions<AcquisitionDbContext> options) : IDbContextFactory<AcquisitionDbContext>
{
	public AcquisitionDbContext CreateDbContext() => new(options);
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class SingleServerJobStorageTests
{
	[Fact]
	public void DurableStorageDefaultsToSingleServerMode()
	{
		var durable = new InMemoryJobStorage(TimeProvider.System);
		var services = new ServiceCollection();
		_ = services.AddImmediateJobsCore(options => options.UseStorage(_ => durable));

		using var provider = services.BuildServiceProvider();
		var storage = Assert.IsType<SingleServerJobStorage>(provider.GetRequiredService<IJobStorage>());

		Assert.Same(durable, storage.DurableStorage);
		_ = Assert.IsType<InMemoryJobStorage>(storage.PrimaryStorage);
	}

	[Fact]
	public void InMemoryAndDistributedModesRemainDirect()
	{
		var durable = new InMemoryJobStorage(TimeProvider.System);
		var inMemoryServices = new ServiceCollection();
		_ = inMemoryServices.AddImmediateJobsCore(options => options.UseInMemory());
		using var inMemoryProvider = inMemoryServices.BuildServiceProvider();
		_ = Assert.IsType<InMemoryJobStorage>(inMemoryProvider.GetRequiredService<IJobStorage>());

		var distributedServices = new ServiceCollection();
		_ = distributedServices.AddImmediateJobsCore(options => options.UseStorage(_ => durable).UseDistributed());
		using var distributedProvider = distributedServices.BuildServiceProvider();
		Assert.Same(durable, distributedProvider.GetRequiredService<IJobStorage>());
	}

	[Fact]
	public void ExplicitDurableModeRequiresAProvider()
	{
		var singleServerServices = new ServiceCollection();
		_ = Assert.Throws<InvalidOperationException>(() =>
			singleServerServices.AddImmediateJobsCore(options => options.UseSingleServer())
		);

		var distributedServices = new ServiceCollection();
		_ = Assert.Throws<InvalidOperationException>(() =>
			distributedServices.AddImmediateJobsCore(options => options.UseDistributed())
		);
	}

	[Fact]
	public async Task EnqueuedJobsAndSchedulesAreWrittenThroughAndRecovered()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var durable = new InMemoryJobStorage(timeProvider);
		using var firstProcess = new SingleServerJobStorage(durable, timeProvider);
		var job = CreateJob(timeProvider.GetUtcNow() + TimeSpan.FromHours(1));
		var schedule = new RecurringJobSchedule
		{
			Name = "hourly",
			JobName = "example",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			IsPaused = true,
			NextRunAt = timeProvider.GetUtcNow() + TimeSpan.FromHours(1),
		};

		await firstProcess.EnqueueAsync(job, cancellationToken);
		await firstProcess.UpsertRecurringAsync(schedule, cancellationToken);

		Assert.Equal(job.Id, Assert.Single(await durable.QueryJobsAsync(new(), cancellationToken)).Id);
		Assert.Equal(schedule.Name, Assert.Single((await durable.GetMonitoringSnapshotAsync(cancellationToken)).Recurring).Name);

		using var restartedProcess = new SingleServerJobStorage(durable, timeProvider);
		await restartedProcess.InitializeAsync(cancellationToken);

		var recoveredJob = Assert.Single(await restartedProcess.QueryJobsAsync(new(), cancellationToken));
		var recoveredSchedule = Assert.Single((await restartedProcess.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
		Assert.Equal(job, recoveredJob);
		Assert.Equal(schedule, recoveredSchedule);
	}

	[Fact]
	public async Task WorkingJobIsRecoveredAndReacquiredAfterItsLeaseExpires()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var durable = new InMemoryJobStorage(timeProvider);
		using var firstProcess = new SingleServerJobStorage(durable, timeProvider);
		var job = CreateJob(timeProvider.GetUtcNow());
		await firstProcess.EnqueueAsync(job, cancellationToken);
		_ = Assert.Single(await firstProcess.AcquireDueJobsAsync(CreateRequest("first"), cancellationToken));

		using var restartedProcess = new SingleServerJobStorage(durable, timeProvider);
		Assert.Empty(await restartedProcess.AcquireDueJobsAsync(CreateRequest("second"), cancellationToken));

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		var recovered = Assert.Single(
			await restartedProcess.AcquireDueJobsAsync(CreateRequest("second"), cancellationToken)
		);

		Assert.Equal(job.Id, recovered.Id);
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("second", recovered.WorkerId);
		var durableRecord = Assert.Single(await durable.QueryJobsAsync(new(), cancellationToken));
		Assert.Equal(recovered.State, durableRecord.State);
		Assert.Equal(recovered.Attempt, durableRecord.Attempt);
		Assert.Equal(recovered.WorkerId, durableRecord.WorkerId);
	}

	[Fact]
	public async Task HeartbeatRemainsInMemoryAndIsNotWrittenToDurableStorage()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var durable = new InMemoryJobStorage(timeProvider);
		using var storage = new SingleServerJobStorage(durable, timeProvider);
		var server = new JobServerSnapshot(
			"single-server",
			timeProvider.GetUtcNow(),
			ActiveWorkers: 2,
			MaxWorkers: 4
		);

		await storage.HeartbeatAsync(server, cancellationToken);

		var primarySnapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		var durableSnapshot = await durable.GetMonitoringSnapshotAsync(cancellationToken);
		Assert.Equal(server, Assert.Single(primarySnapshot.Servers));
		Assert.Empty(durableSnapshot.Servers);
	}

	private static JobAcquisitionRequest CreateRequest(string workerId) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = 1,
		Queues = [new() { QueueName = JobQueueDefinition.DefaultName, Capacity = 1, JobCapacities = new Dictionary<string, int> { ["example"] = 1 } }],
	};

	private static JobRecord CreateJob(DateTimeOffset dueAt) => new()
	{
		Id = Guid.NewGuid(),
		JobName = "example",
		Payload = "{}",
		State = dueAt <= DateTimeOffset.UnixEpoch ? JobState.Pending : JobState.Scheduled,
		DueAt = dueAt,
		CreatedAt = DateTimeOffset.UnixEpoch,
	};
}
#pragma warning restore CS1591

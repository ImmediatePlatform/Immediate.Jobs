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
		_ = Assert.Throws<ImmediateJobException>(() =>
			singleServerServices.AddImmediateJobsCore(options => options.UseSingleServer())
		);

		var distributedServices = new ServiceCollection();
		_ = Assert.Throws<ImmediateJobException>(() =>
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
	public async Task ExecutionTelemetryIsWrittenThroughToDurableStorage()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var durable = new InMemoryJobStorage(timeProvider);
		using var storage = new SingleServerJobStorage(durable, timeProvider);
		var job = CreateJob(timeProvider.GetUtcNow());
		await storage.EnqueueAsync(job, cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));

		var startedAt = timeProvider.GetUtcNow();
		await storage.SetExecutionTelemetryAsync(
			job.Id,
			"worker",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			startedAt,
			cancellationToken
		);

		var primaryJob = Assert.Single(await storage.QueryJobsAsync(new() { Id = job.Id }, cancellationToken));
		var durableJob = Assert.Single(await durable.QueryJobsAsync(new() { Id = job.Id }, cancellationToken));
		Assert.Equal(primaryJob.ExecutionTraceId, durableJob.ExecutionTraceId);
		Assert.Equal(primaryJob.ExecutionSpanId, durableJob.ExecutionSpanId);
		Assert.Equal(primaryJob.ExecutionStartedAt, durableJob.ExecutionStartedAt);
	}

	[Fact]
	public async Task ChainedBatchesRestoreParentBeforeFollowUpBatch()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var durable = new InMemoryJobStorage(timeProvider);
		var parentJob = CreateJob(timeProvider.GetUtcNow()) with
		{
			Id = "parent-job",
			BatchId = "parent-batch",
			State = JobState.Succeeded,
			CompletedAt = timeProvider.GetUtcNow(),
		};
		await durable.EnqueueBatchAsync(
			new()
			{
				Id = "parent-batch",
				CreatedAt = timeProvider.GetUtcNow(),
				TotalJobs = 1,
				PendingCount = 0,
				SucceededCount = 1,
				CompletedAt = timeProvider.GetUtcNow(),
				State = BatchState.Succeeded,
			},
			[parentJob],
			[],
			cancellationToken
		);

		var childJob = CreateJob(timeProvider.GetUtcNow()) with
		{
			Id = "child-job",
			BatchId = "child-batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await durable.EnqueueBatchAsync(
			new()
			{
				Id = "child-batch",
				CreatedAt = timeProvider.GetUtcNow().AddTicks(1),
				TotalJobs = 1,
				PendingCount = 1,
				State = BatchState.Executing,
			},
			[childJob],
			[new()
			{
				ChildJobId = childJob.Id,
				ParentBatchId = "parent-batch",
			}],
			cancellationToken
		);

		using var restartedProcess = new SingleServerJobStorage(durable, timeProvider);
		await restartedProcess.InitializeAsync(cancellationToken);

		var graph = Assert.IsType<BatchGraph>(
			await restartedProcess.GetBatchGraphAsync("child-batch", cancellationToken)
		);
		Assert.Equal("parent-batch", Assert.Single(graph.Edges).ParentBatchId);
		Assert.Equal(
			childJob.Id,
			Assert.Single(await restartedProcess.AcquireDueJobsAsync(CreateRequest("restarted"), cancellationToken)).Id
		);
	}

	[Fact]
	public async Task CodeDefinedScheduleCannotBeReplacedByDynamicScheduleInMemory()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var storage = new InMemoryJobStorage(timeProvider);
		var codeDefined = new RecurringJobSchedule
		{
			Name = "cleanup",
			JobName = "cleanup",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			IsPaused = true,
			NextRunAt = timeProvider.GetUtcNow() + TimeSpan.FromHours(1),
		};
		await storage.UpsertRecurringAsync(codeDefined, cancellationToken);

		var exception = await Assert.ThrowsAsync<ImmediateJobException>(() =>
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
	public async Task ObsoleteCodeDefinedSchedulesAreRemovedFromPrimaryAndDurableStorage(bool preserveCurrent)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var durable = new InMemoryJobStorage(timeProvider);
		using var storage = new SingleServerJobStorage(durable, timeProvider);
		var current = CreateSchedule("current", isCodeDefined: true, timeProvider);
		var obsolete = CreateSchedule("obsolete", isCodeDefined: true, timeProvider);
		var dynamic = CreateSchedule("dynamic", isCodeDefined: false, timeProvider);
		await storage.UpsertRecurringAsync(current, cancellationToken);
		await storage.UpsertRecurringAsync(obsolete, cancellationToken);
		await storage.UpsertRecurringAsync(dynamic, cancellationToken);

		await storage.RemoveObsoleteCodeDefinedRecurringAsync(
			preserveCurrent ? [current.Name] : [],
			cancellationToken
		);

		var expectedNames = preserveCurrent ? ["current", "dynamic"] : new[] { "dynamic" };
		var primaryNames = (await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring
			.Select(static schedule => schedule.Name)
			.Order(StringComparer.Ordinal);
		var durableNames = (await durable.GetMonitoringSnapshotAsync(cancellationToken)).Recurring
			.Select(static schedule => schedule.Name)
			.Order(StringComparer.Ordinal);
		Assert.Equal(expectedNames.Order(StringComparer.Ordinal), primaryNames);
		Assert.Equal(expectedNames.Order(StringComparer.Ordinal), durableNames);
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
		Id = Guid.NewGuid().ToString("N"),
		JobName = "example",
		Payload = "{}",
		State = dueAt <= DateTimeOffset.UnixEpoch ? JobState.Pending : JobState.Scheduled,
		DueAt = dueAt,
		CreatedAt = DateTimeOffset.UnixEpoch,
	};

	private static RecurringJobSchedule CreateSchedule(
		string name,
		bool isCodeDefined,
		TimeProvider timeProvider
	) => new()
	{
		Name = name,
		JobName = "example",
		Cron = "0 * * * *",
		TimeZone = "UTC",
		IsCodeDefined = isCodeDefined,
		NextRunAt = timeProvider.GetUtcNow() + TimeSpan.FromHours(1),
	};
}
#pragma warning restore CS1591

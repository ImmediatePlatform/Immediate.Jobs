using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class SingleServerJobStorageTests
{
	[Fact]
	public async Task DurableStorageDefaultsToSingleServerMode()
	{
		await using var durable = new InMemoryJobStorage(TimeProvider.System);
		var services = new ServiceCollection();
		_ = services.AddImmediateJobsCore(options => options.UseStorage(_ => durable));

		await using var provider = services.BuildServiceProvider();
		var storage = Assert.IsType<SingleServerJobStorage>(provider.GetRequiredService<IJobStorage>());

		Assert.Same(durable, storage.DurableStorage);
		_ = Assert.IsType<InMemoryJobStorage>(storage.PrimaryStorage);
	}

	[Fact]
	public async Task InMemoryAndDistributedModesRemainDirect()
	{
		await using var durable = new InMemoryJobStorage(TimeProvider.System);
		var inMemoryServices = new ServiceCollection();
		_ = inMemoryServices.AddImmediateJobsCore(options => options.UseInMemory());
		await using var inMemoryProvider = inMemoryServices.BuildServiceProvider();
		_ = Assert.IsType<InMemoryJobStorage>(inMemoryProvider.GetRequiredService<IJobStorage>());

		var distributedServices = new ServiceCollection();
		_ = distributedServices.AddImmediateJobsCore(options => options.UseStorage(_ => durable).UseDistributed());
		await using var distributedProvider = distributedServices.BuildServiceProvider();
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
	public async Task ConcurrentDisposalIsIdempotent()
	{
		await using var durable = new InMemoryJobStorage(TimeProvider.System);
		var storage = new SingleServerJobStorage(durable, TimeProvider.System);

		await Task.WhenAll(
			Enumerable.Range(0, 8)
				.Select(_ => storage.DisposeAsync().AsTask())
		);

		await storage.DisposeAsync();
	}

	[Fact]
	public void SynchronousDisposalIsIdempotent()
	{
#pragma warning disable CA2000 // SingleServerJobStorage owns and disposes the durable storage.
		using var storage = new SingleServerJobStorage(new InMemoryJobStorage(TimeProvider.System), TimeProvider.System);
#pragma warning restore CA2000

		storage.Dispose();
		storage.Dispose();
	}

	[Fact]
	public async Task InitializationWaitersCompleteSafelyWhenDisposalStarts()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var inner = new InMemoryJobStorage(TimeProvider.System);
		await using var proxy = CreateProxy(inner);
		var proxyState = (DurableStorageProxy)(object)proxy;
		proxyState.BlockInitialization = true;
		await using var storage = new SingleServerJobStorage(proxy, TimeProvider.System);
		var initializing = storage.InitializeAsync(cancellationToken).AsTask();
		await proxyState.InitializationEntered.Task.WaitAsync(cancellationToken);
		var waiting = storage.InitializeAsync(cancellationToken).AsTask();
		await Task.Yield();
		var disposing = storage.DisposeAsync().AsTask();

		_ = proxyState.InitializationRelease.TrySetResult();

		await initializing.WaitAsync(cancellationToken);
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
		await disposing.WaitAsync(cancellationToken);
	}

	[Fact]
	public async Task RecoveryBulkLoadsIncomingEdgesOncePerNonEmptyPage()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var inner = new InMemoryJobStorage(timeProvider);
		for (var index = 0; index < 1001; index++)
		{
			await inner.EnqueueAsync(
				CreateJob(timeProvider.GetUtcNow()) with { JobId = string.Create(CultureInfo.InvariantCulture, $"recovered-{index:D4}") },
				cancellationToken
			);
		}

		await using var proxy = CreateProxy(inner);
		var proxyState = (DurableStorageProxy)(object)proxy;
		await using var storage = new SingleServerJobStorage(proxy, timeProvider);

		await storage.InitializeAsync(cancellationToken);

		Assert.Equal(2, proxyState.GetIncomingEdgesCalls);
		Assert.Equal(0, proxyState.GetJobStatusCalls);
		Assert.Equal(1000, (await storage.QueryJobsAsync(new() { Take = 1000 }, cancellationToken)).Count);
		_ = Assert.Single(await storage.QueryJobsAsync(new() { Skip = 1000, Take = 1 }, cancellationToken));
	}

	[Fact]
	public async Task RecoveryRestoresStandaloneContinuationsAfterTheirParents()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durable = new InMemoryJobStorage(timeProvider);
		var parent = CreateJob(timeProvider.GetUtcNow()) with { JobId = "standalone-parent" };
		var child = CreateJob(timeProvider.GetUtcNow().AddTicks(1)) with
		{
			JobId = "standalone-child",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await durable.EnqueueAsync(parent, cancellationToken);
		await durable.EnqueueContinuationAsync(child, [new()
		{
			ChildJobId = child.JobId,
			ParentJobId = parent.JobId,
			Trigger = ContinuationTrigger.Success,
		}], cancellationToken);
		await using var storage = new SingleServerJobStorage(durable, timeProvider);

		await storage.InitializeAsync(cancellationToken);

		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(parent.JobId, cancellationToken))!.State);
		var childStatus = Assert.IsType<JobStatus>(await storage.GetJobStatusAsync(child.JobId, cancellationToken));
		Assert.Equal(JobState.AwaitingContinuation, childStatus.State);
		_ = Assert.Single(childStatus.DependsOn);
	}

	[Fact]
	public async Task TelemetryPersistenceFailureDoesNotConsumeAnAttempt()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var inner = new InMemoryJobStorage(TimeProvider.System);
		await using var proxy = CreateProxy(inner);
		((DurableStorageProxy)(object)proxy).FailTelemetry = true;
		var state = new BatchWorkflowState();
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IJobStorage>(proxy);
		_ = services.AddSingleton(state);
		_ = services.AddSingleton(new DynamicExpansionState());
		_ = services.AddSingleton(new ExecutionBufferProbeState());
		_ = services.AddImmediateJobsCore();
		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
		await using var provider = services.BuildServiceProvider();
		var scheduler = provider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var service = provider.GetRequiredService<JobSchedulingService>();
		var handle = await scheduler.EnqueueAsync(new("telemetry-failure"), cancellationToken);

		await service.DrainAsync(cancellationToken);

		var job = Assert.Single(await inner.QueryJobsAsync(new() { Id = handle.Id }, cancellationToken));
		Assert.Equal(JobState.Succeeded, job.State);
		Assert.Equal(1, job.Attempt);
		Assert.Equal(["telemetry-failure"], state.Events);
	}

	[Fact]
	public async Task UnknownAcquiredJobIsFailedWithoutAnInvoker()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var inner = new InMemoryJobStorage(TimeProvider.System);
		await using var proxy = CreateProxy(inner);
		var proxyState = (DurableStorageProxy)(object)proxy;
		proxyState.CaptureFailures = true;
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IJobStorage>(proxy);
		_ = services.AddImmediateJobsCore();
		await using var provider = services.BuildServiceProvider();
		var service = provider.GetRequiredService<JobSchedulingService>();
		var now = TimeProvider.System.GetUtcNow();

		await service.ExecuteSingleAsync(
			new()
			{
				JobId = "unknown-job",
				JobName = "unknown-definition",
				Payload = "{}",
				State = JobState.Active,
				DueAt = now,
				CreatedAt = now,
			},
			cancellationToken
		);

		Assert.Equal("unknown-job", proxyState.CapturedFailedJobId);
		var failure = Assert.IsType<string>(proxyState.CapturedFailure);
		Assert.Contains("No generated job definition", failure, StringComparison.Ordinal);
		Assert.Contains("unknown-definition", failure, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HostShutdownDuringTelemetryKeepsCancellationSemantics()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var inner = new InMemoryJobStorage(TimeProvider.System);
		await using var proxy = CreateProxy(inner);
		using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		((DurableStorageProxy)(object)proxy).CancelTelemetry = shutdown;
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IJobStorage>(proxy);
		_ = services.AddSingleton(new BatchWorkflowState());
		_ = services.AddSingleton(new DynamicExpansionState());
		_ = services.AddSingleton(new ExecutionBufferProbeState());
		_ = services.AddImmediateJobsCore();
		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
		await using var provider = services.BuildServiceProvider();
		var scheduler = provider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var service = provider.GetRequiredService<JobSchedulingService>();
		var handle = await scheduler.EnqueueAsync(new("host-shutdown"), cancellationToken);

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => service.DrainAsync(shutdown.Token).AsTask()
		);

		var job = Assert.Single(await inner.QueryJobsAsync(new() { Id = handle.Id }, cancellationToken));
		Assert.Equal(JobState.Active, job.State);
		Assert.Equal(1, job.Attempt);
	}

	[Fact]
	public async Task EnqueuedJobsAndSchedulesAreWrittenThroughAndRecovered()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durable = new InMemoryJobStorage(timeProvider);
		await using var firstProcess = new SingleServerJobStorage(durable, timeProvider);
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

		Assert.Equal(job.JobId, Assert.Single(await durable.QueryJobsAsync(new(), cancellationToken)).JobId);
		Assert.Equal(schedule.Name, Assert.Single((await durable.GetMonitoringSnapshotAsync(cancellationToken)).Recurring).Name);

		await using var restartedProcess = new SingleServerJobStorage(durable, timeProvider);
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
		await using var durable = new InMemoryJobStorage(timeProvider);
		await using var storage = new SingleServerJobStorage(durable, timeProvider);
		var job = CreateJob(timeProvider.GetUtcNow());
		await storage.EnqueueAsync(job, cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));

		var startedAt = timeProvider.GetUtcNow();
		await storage.SetExecutionTelemetryAsync(
			job.JobId,
			1, "worker",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			startedAt,
			cancellationToken
		);

		var primaryJob = Assert.Single(await storage.QueryJobsAsync(new() { Id = job.JobId }, cancellationToken));
		var durableJob = Assert.Single(await durable.QueryJobsAsync(new() { Id = job.JobId }, cancellationToken));
		Assert.Equal(primaryJob.ExecutionTraceId, durableJob.ExecutionTraceId);
		Assert.Equal(primaryJob.ExecutionSpanId, durableJob.ExecutionSpanId);
		Assert.Equal(primaryJob.ExecutionStartedAt, durableJob.ExecutionStartedAt);
		var primaryExecution = Assert.Single(await storage.PrimaryStorage.QueryJobExecutionsAsync(
			new() { JobId = job.JobId },
			cancellationToken
		));
		var durableExecution = Assert.Single(await durable.QueryJobExecutionsAsync(
			new() { JobId = job.JobId },
			cancellationToken
		));
		Assert.Equal(primaryExecution, durableExecution);
		Assert.Equal(JobExecutionState.Active, durableExecution.State);
	}

	[Fact]
	public async Task ChainedBatchesRestoreParentBeforeFollowUpBatch()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durable = new InMemoryJobStorage(timeProvider);
		var parentJob = CreateJob(timeProvider.GetUtcNow()) with
		{
			JobId = "parent-job",
			BatchId = "parent-batch",
			State = JobState.Succeeded,
			CompletedAt = timeProvider.GetUtcNow(),
		};
		await durable.EnqueueBatchAsync(
			new()
			{
				BatchId = "parent-batch",
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
			JobId = "child-job",
			BatchId = "child-batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await durable.EnqueueBatchAsync(
			new()
			{
				BatchId = "child-batch",
				CreatedAt = timeProvider.GetUtcNow().AddTicks(1),
				TotalJobs = 1,
				PendingCount = 1,
				State = BatchState.Executing,
			},
			[childJob],
			[new()
			{
				ChildJobId = childJob.JobId,
				ParentBatchId = "parent-batch",
			}],
			cancellationToken
		);

		await using var restartedProcess = new SingleServerJobStorage(durable, timeProvider);
		await restartedProcess.InitializeAsync(cancellationToken);

		var graph = Assert.IsType<BatchGraph>(
			await restartedProcess.GetBatchGraphAsync("child-batch", cancellationToken)
		);
		Assert.Equal("parent-batch", Assert.Single(graph.Edges).ParentBatchId);
		Assert.Equal(
			childJob.JobId,
			Assert.Single(await restartedProcess.AcquireDueJobsAsync(CreateRequest("restarted"), cancellationToken)).JobId
		);
	}

	[Fact]
	public async Task CodeDefinedScheduleCannotBeReplacedByDynamicScheduleInMemory()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(timeProvider);
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
		await using var durable = new InMemoryJobStorage(timeProvider);
		await using var storage = new SingleServerJobStorage(durable, timeProvider);
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
		await using var durable = new InMemoryJobStorage(timeProvider);
		await using var firstProcess = new SingleServerJobStorage(durable, timeProvider);
		var job = CreateJob(timeProvider.GetUtcNow());
		await firstProcess.EnqueueAsync(job, cancellationToken);
		_ = Assert.Single(await firstProcess.AcquireDueJobsAsync(CreateRequest("first"), cancellationToken));

		await using var restartedProcess = new SingleServerJobStorage(durable, timeProvider);
		Assert.Empty(await restartedProcess.AcquireDueJobsAsync(CreateRequest("second"), cancellationToken));

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		var recovered = Assert.Single(
			await restartedProcess.AcquireDueJobsAsync(CreateRequest("second"), cancellationToken)
		);

		Assert.Equal(job.JobId, recovered.JobId);
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("second", recovered.WorkerId);
		var durableRecord = Assert.Single(await durable.QueryJobsAsync(new(), cancellationToken));
		Assert.Equal(recovered.State, durableRecord.State);
		Assert.Equal(recovered.Attempt, durableRecord.Attempt);
		Assert.Equal(recovered.WorkerId, durableRecord.WorkerId);
		var executions = await restartedProcess.QueryJobExecutionsAsync(
			new() { JobId = job.JobId },
			cancellationToken
		);
		Assert.Collection(
			executions,
			execution =>
			{
				Assert.Equal(2, execution.Attempt);
				Assert.Equal(JobExecutionState.Active, execution.State);
			},
			execution =>
			{
				Assert.Equal(1, execution.Attempt);
				Assert.Equal(JobExecutionState.Interrupted, execution.State);
			}
		);
	}

	[Fact]
	public async Task HeartbeatRemainsInMemoryAndIsNotWrittenToDurableStorage()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durable = new InMemoryJobStorage(timeProvider);
		await using var storage = new SingleServerJobStorage(durable, timeProvider);
		var server = new JobServerSnapshot
		{
			WorkerId = "single-server",
			LastHeartbeat = timeProvider.GetUtcNow(),
			ActiveWorkers = 2,
			MaxWorkers = 4,
		};

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
		JobId = Guid.NewGuid().ToString("N"),
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

	internal static ISingleServerDurableStorage CreateProxy(InMemoryJobStorage inner)
	{
		var proxy = DispatchProxy.Create<ISingleServerDurableStorage, DurableStorageProxy>();
		((DurableStorageProxy)(object)proxy).Inner = inner;
		return proxy;
	}

	public interface ISingleServerDurableStorage : IRecurringJobStorage, IJobGraphStorage, IJobStorageReplica;

	public class DurableStorageProxy : DispatchProxy
	{
		public object Inner { get; set; } = null!;
		public bool BlockInitialization { get; set; }
		public bool BlockBatchEnqueue { get; set; }
		public bool FailTelemetry { get; set; }
		public bool CaptureFailures { get; set; }
		public CancellationTokenSource? CancelTelemetry { get; set; }
		public string? CapturedFailedJobId { get; private set; }
		public string? CapturedFailure { get; private set; }
		public int GetIncomingEdgesCalls { get; private set; }
		public int GetJobStatusCalls { get; private set; }
		public TaskCompletionSource InitializationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource InitializationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource BatchEnqueueEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource BatchEnqueueRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public IReadOnlyList<JobRecord>? CapturedBatchJobs { get; private set; }
		public IReadOnlyList<JobContinuationEdge>? CapturedBatchEdges { get; private set; }

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			ArgumentNullException.ThrowIfNull(targetMethod);
			ArgumentNullException.ThrowIfNull(args);
			if (string.Equals(targetMethod.Name, nameof(IJobStorage.InitializeAsync), StringComparison.Ordinal) && BlockInitialization)
			{
				_ = InitializationEntered.TrySetResult();
				return new ValueTask(InitializationRelease.Task);
			}

			if (string.Equals(targetMethod.Name, nameof(IJobStorage.SetExecutionTelemetryAsync), StringComparison.Ordinal) && FailTelemetry)
#pragma warning disable CA2012 // Reflection proxies must box the ValueTask returned by the intercepted interface method.
				return ValueTask.FromException(new InvalidOperationException("Expected telemetry persistence failure."));
#pragma warning restore CA2012

			if (string.Equals(targetMethod.Name, nameof(IJobStorage.SetExecutionTelemetryAsync), StringComparison.Ordinal) && CancelTelemetry is { } cancellation)
			{
				cancellation.Cancel();
#pragma warning disable CA2012 // Reflection proxies must box the ValueTask returned by the intercepted interface method.
				return ValueTask.FromCanceled((CancellationToken)args[^1]!);
#pragma warning restore CA2012
			}

			if (string.Equals(targetMethod.Name, nameof(IJobStorage.FailAsync), StringComparison.Ordinal) && CaptureFailures)
			{
				CapturedFailedJobId = (string)args[0]!;
				CapturedFailure = (string)args[3]!;
				return ValueTask.CompletedTask;
			}

			if (string.Equals(targetMethod.Name, nameof(IJobGraphStorage.EnqueueBatchAsync), StringComparison.Ordinal) && BlockBatchEnqueue)
			{
				CapturedBatchJobs = (IReadOnlyList<JobRecord>)args[1]!;
				CapturedBatchEdges = (IReadOnlyList<JobContinuationEdge>)args[2]!;
				_ = BatchEnqueueEntered.TrySetResult();
				return new ValueTask(BatchEnqueueRelease.Task);
			}

			if (string.Equals(targetMethod.Name, nameof(IJobGraphStorage.GetIncomingEdgesAsync), StringComparison.Ordinal))
				GetIncomingEdgesCalls++;
			if (string.Equals(targetMethod.Name, nameof(IJobStorage.GetJobStatusAsync), StringComparison.Ordinal))
				GetJobStatusCalls++;

			try
			{
				return targetMethod.Invoke(Inner, args);
			}
			catch (TargetInvocationException exception) when (exception.InnerException is { } innerException)
			{
				ExceptionDispatchInfo.Capture(innerException).Throw();
				throw;
			}
		}
	}
}
#pragma warning restore CS1591

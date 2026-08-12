using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class SingleServerJobStorageTests
{
	[Fact]
	public async Task DurableStorageDefaultsToSingleServerMode()
	{
		await using var durableInner = new InMemoryJobStorage(TimeProvider.System);
		await using var durable = CreateProxy(durableInner);
		var services = new ServiceCollection();
		_ = services.AddImmediateJobsCore().ConfigureStorage(options => options.UseStorage(_ => durable));

		await using var provider = services.BuildServiceProvider();
		var storage = Assert.IsType<SingleServerJobStorage>(provider.GetRequiredService<IJobStorage>());

		Assert.Same(durable, storage.DurableStorage);
		_ = Assert.IsType<InMemoryJobStorage>(storage.PrimaryStorage);
		_ = Assert.IsAssignableFrom<IFairQueueStorage>(storage);
		Assert.Equal(
			StorageCapabilities.Queue |
			StorageCapabilities.Recurring |
			StorageCapabilities.Graph |
			StorageCapabilities.FairQueues,
			storage.GetCapabilities()
		);
	}

	[Fact]
	public async Task InMemoryAndDistributedModesRemainDirect()
	{
		await using var durable = new InMemoryJobStorage(TimeProvider.System);
		var inMemoryServices = new ServiceCollection();
		_ = inMemoryServices.AddImmediateJobsCore().ConfigureStorage(options => options.UseInMemory());
		await using var inMemoryProvider = inMemoryServices.BuildServiceProvider();
		_ = Assert.IsType<InMemoryJobStorage>(inMemoryProvider.GetRequiredService<IJobStorage>());

		var distributedServices = new ServiceCollection();
		_ = distributedServices.AddImmediateJobsCore().ConfigureStorage(options => options.UseStorage(_ => durable).UseDistributed());
		await using var distributedProvider = distributedServices.BuildServiceProvider();
		Assert.Same(durable, distributedProvider.GetRequiredService<IJobStorage>());
	}

	[Fact]
	public void ExplicitDurableModeRequiresAProvider()
	{
		var singleServerServices = new ServiceCollection();
		_ = Assert.Throws<ImmediateJobException>(() =>
			singleServerServices.AddImmediateJobsCore().ConfigureStorage(options => options.UseSingleServer())
		);

		var distributedServices = new ServiceCollection();
		_ = Assert.Throws<ImmediateJobException>(() =>
			distributedServices.AddImmediateJobsCore().ConfigureStorage(options => options.UseDistributed())
		);
	}

	[Fact]
	[SuppressMessage("Reliability", "CA2000", Justification = "SingleServerJobStorage owns and disposes the durable proxy and its in-memory test store.")]
	public void SynchronousDisposalIsIdempotent()
	{
		using var storage = new SingleServerJobStorage(
			CreateProxy(new InMemoryJobStorage(TimeProvider.System)),
			TimeProvider.System
		);

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
				CreateJob(timeProvider.GetUtcNow()) with { Id = string.Create(CultureInfo.InvariantCulture, $"recovered-{index:D4}") },
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
		await using var durableInner = new InMemoryJobStorage(timeProvider);
		await using var durable = CreateProxy(durableInner);
		var parent = CreateJob(timeProvider.GetUtcNow()) with { Id = "standalone-parent" };
		var child = CreateJob(timeProvider.GetUtcNow().AddTicks(1)) with
		{
			Id = "standalone-child",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await durable.EnqueueAsync(parent, cancellationToken);
		await durable.EnqueueContinuationAsync(child, [new()
		{
			ChildJobId = child.Id,
			ParentJobId = parent.Id,
			Trigger = ContinuationTrigger.Success,
		}], cancellationToken);
		await using var storage = new SingleServerJobStorage(durable, timeProvider);

		await storage.InitializeAsync(cancellationToken);

		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(parent.Id, cancellationToken))!.State);
		var childStatus = Assert.IsType<JobStatus>(await storage.GetJobStatusAsync(child.Id, cancellationToken));
		Assert.Equal(JobState.AwaitingContinuation, childStatus.State);
		_ = Assert.Single(childStatus.DependsOn);
	}

	[Fact]
	public async Task EnqueuedJobsAndSchedulesAreWrittenThroughAndRecovered()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durableInner = new InMemoryJobStorage(timeProvider);
		await using var durable = CreateProxy(durableInner);
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

		Assert.Equal(job.Id, Assert.Single(await durable.QueryJobsAsync(new(), cancellationToken)).Id);
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
		await using var durableInner = new InMemoryJobStorage(timeProvider);
		await using var durable = CreateProxy(durableInner);
		await using var storage = new SingleServerJobStorage(durable, timeProvider);
		var job = CreateJob(timeProvider.GetUtcNow());
		await storage.EnqueueAsync(job, cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));

		var startedAt = timeProvider.GetUtcNow();
		await storage.SetExecutionTelemetryAsync(
			job.Id,
			1, "worker",
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
		var primaryExecution = Assert.Single(await storage.PrimaryStorage.QueryJobExecutionsAsync(
			new() { JobId = job.Id },
			cancellationToken
		));
		var durableExecution = Assert.Single(await durable.QueryJobExecutionsAsync(
			new() { JobId = job.Id },
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
		await using var durableInner = new InMemoryJobStorage(timeProvider);
		await using var durable = CreateProxy(durableInner);
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

		await using var restartedProcess = new SingleServerJobStorage(durable, timeProvider);
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
	public async Task WorkingJobIsRecoveredAndReacquiredAfterItsLeaseExpires()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durableInner = new InMemoryJobStorage(timeProvider);
		await using var durable = CreateProxy(durableInner);
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

		Assert.Equal(job.Id, recovered.Id);
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("second", recovered.WorkerId);
		var durableRecord = Assert.Single(await durable.QueryJobsAsync(new(), cancellationToken));
		Assert.Equal(recovered.State, durableRecord.State);
		Assert.Equal(recovered.Attempt, durableRecord.Attempt);
		Assert.Equal(recovered.WorkerId, durableRecord.WorkerId);
		var executions = await restartedProcess.QueryJobExecutionsAsync(
			new() { JobId = job.Id },
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
		await using var durableInner = new InMemoryJobStorage(timeProvider);
		await using var durable = CreateProxy(durableInner);
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
		Id = Guid.NewGuid().ToString("N"),
		JobName = "example",
		Payload = "{}",
		State = dueAt <= DateTimeOffset.UnixEpoch ? JobState.Pending : JobState.Scheduled,
		DueAt = dueAt,
		CreatedAt = DateTimeOffset.UnixEpoch,
	};

	private static ISingleServerDurableStorage CreateProxy(InMemoryJobStorage inner)
	{
		var proxy = DispatchProxy.Create<ISingleServerDurableStorage, DurableStorageProxy>();
		((DurableStorageProxy)(object)proxy).Inner = inner;
		return proxy;
	}

	public interface ISingleServerDurableStorage : IRecurringJobStorage, IJobGraphStorage, IJobStorageReplica, IJobGraphStorageReplica;

	public class DurableStorageProxy : DispatchProxy
	{
		private readonly List<JobContinuationEdge> _edges = [];
		public object Inner { get; set; } = null!;
		public bool BlockInitialization { get; set; }
		public int GetIncomingEdgesCalls { get; private set; }
		public int GetJobStatusCalls { get; private set; }
		public TaskCompletionSource InitializationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource InitializationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		[SuppressMessage("Reliability", "CA2012", Justification = "DispatchProxy must return the boxed ValueTask expected by the interface method.")]
		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			ArgumentNullException.ThrowIfNull(targetMethod);
			ArgumentNullException.ThrowIfNull(args);
			if (string.Equals(targetMethod.Name, nameof(IJobStorage.InitializeAsync), StringComparison.Ordinal) && BlockInitialization)
			{
				_ = InitializationEntered.TrySetResult();
				return new ValueTask(InitializationRelease.Task);
			}

			if (string.Equals(targetMethod.Name, nameof(IJobGraphStorageReplica.GetIncomingEdgesAsync), StringComparison.Ordinal))
			{
				GetIncomingEdgesCalls++;
				var requested = ((IReadOnlyCollection<JobHandle>)args[0]!).Select(static job => job.Id).ToHashSet(StringComparer.Ordinal);
				return ValueTask.FromResult<IReadOnlyList<JobContinuationEdge>>(
					[.. _edges.Where(edge => requested.Contains(edge.ChildJobId))]
				);
			}

			if (string.Equals(targetMethod.Name, nameof(IJobGraphStorage.EnqueueContinuationAsync), StringComparison.Ordinal))
				_edges.AddRange((IReadOnlyList<JobContinuationEdge>)args[1]!);
			if (string.Equals(targetMethod.Name, nameof(IJobGraphStorage.EnqueueBatchAsync), StringComparison.Ordinal))
				_edges.AddRange((IReadOnlyList<JobContinuationEdge>)args[2]!);
			if (string.Equals(targetMethod.Name, nameof(IJobStorage.GetJobStatusAsync), StringComparison.Ordinal))
				GetJobStatusCalls++;
			if (string.Equals(targetMethod.Name, nameof(IJobStorageReplica.AcquireJobsAsync), StringComparison.Ordinal))
			{
				return ((InMemoryJobStorage)Inner).AcquireJobsAsync(
					(IReadOnlyCollection<string>)args[0]!,
					(string)args[1]!,
					(TimeSpan)args[2]!,
					(CancellationToken)args[3]!
				);
			}

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

using System.Collections.Concurrent;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests;

public sealed class QueueSchedulerTests
{
	[Fact]
	public async Task StandaloneInMemorySchedulerDoesNotRenewOrReacquireLongRunningWork()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var execution = new ProcessOwnedExecution();
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<TimeProvider>(clock);
		services.AddSingleton(new JobDefinition
		{
			Name = "process-owned",
			JobType = typeof(ProcessOwnedExecution),
			Invoker = execution,
		});
		services.AddImmediateJobsCore()
			.ConfigureStorage(options => options.UseInMemory())
			.ConfigureWorkers(options =>
			{
				options.WorkerCount = 2;
				options.PollingInterval = TimeSpan.FromSeconds(1);
				options.LeaseDuration = TimeSpan.FromSeconds(3);
			});
		await using var provider = services.BuildServiceProvider();
		var storage = provider.GetRequiredService<IJobStorage>();
		var scheduler = provider.GetRequiredService<JobSchedulingService>();
		await scheduler.StartAsync(token);
		try
		{
			await storage.EnqueueAsync(new()
			{
				JobHandle = JobHandle.FromString("long-running"),
				JobName = "process-owned",
				Payload = "{}",
				State = JobState.Pending,
				CreatedAt = clock.GetUtcNow(),
				DueAt = clock.GetUtcNow(),
			}, token);
			clock.Advance(TimeSpan.FromSeconds(1));
			await execution.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
			clock.Advance(TimeSpan.FromDays(1));
			var observedAfter = clock.GetUtcNow();
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
			deadline.CancelAfter(TimeSpan.FromSeconds(5));
			JobServerSnapshot? server;
			do
			{
				await Task.Delay(1, deadline.Token);
				server = (await storage.GetMonitoringSnapshotAsync(deadline.Token)).Servers.SingleOrDefault();
				clock.Advance(TimeSpan.FromSeconds(1));
			} while (server is null || server.LastHeartbeat < observedAfter || server.Acquisition.LastSucceededAt is not { } succeededAt || succeededAt < observedAfter);

			Assert.Null(server.LeaseRenewal.LastAttemptedAt);
			var job = Assert.Single(await storage.QueryJobsAsync(new(), token));
			Assert.Null(job.LeaseExpiresAt);
			Assert.Equal(JobState.Active, job.State);
			Assert.Equal(1, job.Attempt);
			Assert.Equal(1, execution.Invocations);
		}
		finally
		{
			execution.Release.TrySetResult();
			await scheduler.StopAsync(token);
		}

		Assert.Equal(JobState.Succeeded, Assert.Single(await storage.QueryJobsAsync(new(), token)).State);
	}

	private sealed class ProcessOwnedExecution : IJobInvoker
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _invocations;
		public int Invocations => Volatile.Read(ref _invocations);
		public async ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution)
		{
			Interlocked.Increment(ref _invocations);
			Started.TrySetResult();
			await Release.Task.WaitAsync(execution.CancellationToken);
		}
	}

	[Fact]
	public async Task RepeatedRuntimeRegistrationPreservesOtherHostedServicesAndAddsOneScheduler()
	{
		await using var harness = new JobTestHarness(
			services =>
			{
				services.AddHostedService<OtherHostedService>();
				services.AddImmediateJobsCore();
				services.AddImmediateJobsCore();
			}
		);

		Assert.Collection(
			harness.Services.GetServices<IHostedService>(),
			sd => Assert.IsType<OtherHostedService>(sd),
			sd => Assert.IsType<JobSchedulingService>(sd)
		);
	}

	[Fact]
	public async Task SchedulerAppliesQueueAndJobLimitsBeforeDispatch()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var execution = new BlockingExecution();
		var highQueue = new JobQueueDefinition { Name = "high", Priority = 10, Concurrency = 2 };
		var lowQueue = new JobQueueDefinition { Name = "low", Priority = 0 };
		await using var harness = new JobTestHarness(
			services =>
			{
				services.AddSingleton(new JobDefinition
				{
					Name = "high-a",
					Queue = highQueue,
					MaxConcurrency = 1,
					Invoker = execution,
					JobType = typeof(BlockingExecution),
				});
				services.AddSingleton(new JobDefinition
				{
					Name = "high-b",
					Queue = highQueue,
					Invoker = execution,
					JobType = typeof(BlockingExecution),
				});
				services.AddSingleton(new JobDefinition
				{
					Name = "low-a",
					Queue = lowQueue,
					Invoker = execution,
					JobType = typeof(BlockingExecution),
				});
			},
			workers =>
			{
				workers.WorkerCount = 3;
				workers.PollingInterval = TimeSpan.FromMilliseconds(10);
			}
		);

		var storage = harness.Storage;
		await Enqueue("high", "high-a", 0);
		await Enqueue("high", "high-a", 1);
		await Enqueue("high", "high-b", 2);
		await Enqueue("low", "low-a", 3);
		await Enqueue("low", "low-a", 4);

		await harness.Scheduler.StartAsync(cancellationToken);
		await execution.ThreeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

		Assert.Equal(2, execution.MaximumByQueue["high"]);
		Assert.Equal(1, execution.MaximumByJob["high-a"]);

		_ = execution.Release.TrySetResult();
		await harness.Scheduler.StopAsync(cancellationToken);

		ValueTask Enqueue(string queueName, string jobName, int order)
		{
			storage.LoadPersistedJobState([
				new()
				{
					JobHandle = JobHandle.FromString(Guid.NewGuid().ToString("N")),
					QueueName = queueName,
					JobName = jobName,
					Payload = "{}",
					State = JobState.Pending,
					DueAt = DateTimeOffset.UnixEpoch,
					CreatedAt = DateTimeOffset.UnixEpoch.AddTicks(order),
				}], [], [], []);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class BlockingExecution : IJobInvoker
	{
		private readonly ConcurrentDictionary<string, int> _activeByQueue = new(StringComparer.Ordinal);
		private readonly ConcurrentDictionary<string, int> _activeByJob = new(StringComparer.Ordinal);
		private int _started;

		public ConcurrentDictionary<string, int> MaximumByQueue { get; } = new(StringComparer.Ordinal);
		public ConcurrentDictionary<string, int> MaximumByJob { get; } = new(StringComparer.Ordinal);
		public TaskCompletionSource ThreeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution)
		{
			var record = execution.Record;
			var queueName = Assert.IsType<string>(record.QueueName);
			var queueActive = _activeByQueue.AddOrUpdate(queueName, 1, static (_, count) => count + 1);
			var jobActive = _activeByJob.AddOrUpdate(record.JobName, 1, static (_, count) => count + 1);
			_ = MaximumByQueue.AddOrUpdate(queueName, queueActive, (_, maximum) => Math.Max(maximum, queueActive));
			_ = MaximumByJob.AddOrUpdate(record.JobName, jobActive, (_, maximum) => Math.Max(maximum, jobActive));
			if (Interlocked.Increment(ref _started) == 3)
				_ = ThreeStarted.TrySetResult();

			try
			{
				await Release.Task.WaitAsync(execution.CancellationToken);
			}
			finally
			{
				_ = _activeByQueue.AddOrUpdate(queueName, 0, static (_, count) => count - 1);
				_ = _activeByJob.AddOrUpdate(record.JobName, 0, static (_, count) => count - 1);
			}
		}
	}

	public sealed class OtherHostedService : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
}

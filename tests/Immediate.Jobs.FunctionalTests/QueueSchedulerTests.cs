using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Immediate.Jobs.FunctionalTests;

#pragma warning disable CS1591
public sealed class QueueSchedulerTests
{
	[Fact]
	public async Task RepeatedRuntimeRegistrationAddsOneHostedScheduler()
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddImmediateJobsCore();
		_ = services.AddImmediateJobsCore();

		await using var provider = services.BuildServiceProvider();

		_ = Assert.Single(provider.GetServices<IHostedService>());
	}

	[Fact]
	public async Task SchedulerAppliesQueueAndJobLimitsBeforeDispatch()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var execution = new BlockingExecution();
		var highQueue = new JobQueueDefinition { Name = "high", Priority = 10, Concurrency = 2 };
		var lowQueue = new JobQueueDefinition { Name = "low", Priority = 0 };
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddImmediateJobsCore(options =>
		{
			_ = options.UseInMemory();
			options.MaxParallelJobs = 3;
			options.PollingInterval = TimeSpan.FromMilliseconds(10);
		});
		_ = services.AddSingleton(new JobDefinition
		{
			Name = "high-a",
			Queue = highQueue,
			MaxConcurrency = 1,
			Invoker = execution,
			JobType = typeof(BlockingExecution),
		});
		_ = services.AddSingleton(new JobDefinition
		{
			Name = "high-b",
			Queue = highQueue,
			Invoker = execution,
			JobType = typeof(BlockingExecution),
		});
		_ = services.AddSingleton(new JobDefinition
		{
			Name = "low-a",
			Queue = lowQueue,
			Invoker = execution,
			JobType = typeof(BlockingExecution),
		});

		await using var provider = services.BuildServiceProvider();
		var storage = provider.GetRequiredService<IJobStorage>();
		await storage.InitializeAsync(cancellationToken);
		await Enqueue("high", "high-a", 0);
		await Enqueue("high", "high-a", 1);
		await Enqueue("high", "high-b", 2);
		await Enqueue("low", "low-a", 3);
		await Enqueue("low", "low-a", 4);

		var hostedService = provider.GetServices<IHostedService>().Single();
		await hostedService.StartAsync(cancellationToken);
		await execution.ThreeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

		Assert.Equal(2, execution.MaximumByQueue["high"]);
		Assert.Equal(1, execution.MaximumByJob["high-a"]);

		_ = execution.Release.TrySetResult();
		await hostedService.StopAsync(cancellationToken);

		ValueTask Enqueue(string queueName, string jobName, int order) => storage.EnqueueAsync(new()
		{
			Id = Guid.NewGuid().ToString("N"),
			QueueName = queueName,
			JobName = jobName,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = DateTimeOffset.UnixEpoch,
			CreatedAt = DateTimeOffset.UnixEpoch.AddTicks(order),
		}, cancellationToken);
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
			var queueActive = _activeByQueue.AddOrUpdate(record.QueueName, 1, static (_, count) => count + 1);
			var jobActive = _activeByJob.AddOrUpdate(record.JobName, 1, static (_, count) => count + 1);
			_ = MaximumByQueue.AddOrUpdate(record.QueueName, queueActive, (_, maximum) => Math.Max(maximum, queueActive));
			_ = MaximumByJob.AddOrUpdate(record.JobName, jobActive, (_, maximum) => Math.Max(maximum, jobActive));
			if (Interlocked.Increment(ref _started) == 3)
				_ = ThreeStarted.TrySetResult();

			try
			{
				await Release.Task.WaitAsync(execution.CancellationToken);
			}
			finally
			{
				_ = _activeByQueue.AddOrUpdate(record.QueueName, 0, static (_, count) => count - 1);
				_ = _activeByJob.AddOrUpdate(record.JobName, 0, static (_, count) => count - 1);
			}
		}
	}
}
#pragma warning restore CS1591

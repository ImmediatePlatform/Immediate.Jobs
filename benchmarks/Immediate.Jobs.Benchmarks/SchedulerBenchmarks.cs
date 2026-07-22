using BenchmarkDotNet.Attributes;
using Hangfire;
using Hangfire.Common;
using Hangfire.MemoryStorage;
using Quartz;
using Quartz.Impl;
using System.Text.Json.Serialization;

namespace Immediate.Jobs.Benchmarks;

[MemoryDiagnoser]
public class EnqueueBenchmarks
{
	private readonly TimeProvider _timeProvider = TimeProvider.System;
	private BenchmarkScheduler _immediate = null!;
	private IBackgroundJobClient _hangfire = null!;
	private IScheduler _quartz = null!;
	private long _sequence;

	[GlobalSetup]
	public async Task Setup()
	{
		_immediate = new(
			new InMemoryJobStorage(_timeProvider),
			new SystemTextJsonJobSerializer(),
			_timeProvider
		);
		_ = GlobalConfiguration.Configuration.UseMemoryStorage();
		_hangfire = new BackgroundJobClient();
		_quartz = await new StdSchedulerFactory().GetScheduler();
		await _quartz.Start();
	}

	[GlobalCleanup]
	public async Task Cleanup() => await _quartz.Shutdown(waitForJobsToComplete: true);

	[Benchmark(Baseline = true)]
	public ValueTask<JobHandle> ImmediateJobs() => _immediate.EnqueueAsync(new(42));

	[Benchmark]
	public string Hangfire() => _hangfire.Enqueue(() => BenchmarkOperations.Execute(42));

	[Benchmark]
	public async Task<DateTimeOffset> Quartz()
	{
		var sequence = Interlocked.Increment(ref _sequence);
		var job = JobBuilder.Create<QuartzNoOpJob>()
			.WithIdentity($"enqueue-job-{sequence}")
			.UsingJobData("value", 42)
			.Build();
		var trigger = TriggerBuilder.Create()
			.WithIdentity($"enqueue-trigger-{sequence}")
			.StartNow()
			.Build();
		return await _quartz.ScheduleJob(job, trigger);
	}

	private sealed class BenchmarkScheduler(IJobStorage storage, IJobSerializer serializer, TimeProvider timeProvider)
		: JobScheduler<BenchmarkPayload>(
			storage,
			serializer,
			timeProvider,
			"benchmark-job",
			JobQueueDefinition.DefaultName,
			static options => new BenchmarkJsonContext(options).BenchmarkPayload
		);
}

[MemoryDiagnoser]
public class StartupBenchmarks
{
	[Benchmark(Baseline = true)]
	public object ImmediateJobs()
	{
		var storage = new InMemoryJobStorage(TimeProvider.System);
		return new StartupScheduler(storage, new SystemTextJsonJobSerializer(), TimeProvider.System);
	}

	[Benchmark]
	public object Hangfire()
	{
		var storage = new MemoryStorage();
		return new BackgroundJobClient(storage);
	}

	[Benchmark]
	public object Quartz() => new StdSchedulerFactory();

	private sealed class StartupScheduler(IJobStorage storage, IJobSerializer serializer, TimeProvider timeProvider)
		: JobScheduler<BenchmarkPayload>(
			storage,
			serializer,
			timeProvider,
			"benchmark-job",
			JobQueueDefinition.DefaultName,
			static options => new BenchmarkJsonContext(options).BenchmarkPayload
		);
}

[MemoryDiagnoser]
public class DispatchBenchmarks
{
	private static readonly IServiceProvider Services = new EmptyServiceProvider();
	private readonly BenchmarkInvoker _immediateInvoker = new();
	private readonly QuartzNoOpJob _quartzJob = new();
	private readonly Job _hangfireJob = Job.FromExpression(() => BenchmarkOperations.Execute(42));
	private JobExecution _execution = null!;

	[GlobalSetup]
	public void Setup()
	{
		var record = new JobRecord
		{
			Id = Guid.NewGuid().ToString("N"),
			JobName = "benchmark-job",
			Payload = "{\"value\":42}",
			State = JobState.Active,
			DueAt = DateTimeOffset.UtcNow,
			CreatedAt = DateTimeOffset.UtcNow,
			Attempt = 1,
		};
		var definition = new JobDefinition
		{
			Name = record.JobName,
			Invoker = _immediateInvoker,
			JobType = typeof(BenchmarkInvoker),
		};
		_execution = new(record, definition, CancellationToken.None);
	}

	[Benchmark(Baseline = true)]
	public ValueTask ImmediateJobs() => _immediateInvoker.InvokeAsync(Services, _execution);

	[Benchmark]
	public object? Hangfire() => _hangfireJob.Method.Invoke(null, [.. _hangfireJob.Args]);

	[Benchmark]
	public Task Quartz() => _quartzJob.Execute(null!);

	private sealed class BenchmarkInvoker : IJobInvoker
	{
		public ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution)
		{
			BenchmarkOperations.Execute(42);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}

public sealed record BenchmarkPayload(int Value);

[JsonSerializable(typeof(BenchmarkPayload))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;

public static class BenchmarkOperations
{
	private static int Value;

	public static void Execute(int value) => Volatile.Write(ref Value, value);
}

[DisallowConcurrentExecution]
public sealed class QuartzNoOpJob : IJob
{
	public Task Execute(IJobExecutionContext context)
	{
		BenchmarkOperations.Execute(context?.MergedJobDataMap.GetInt("value") ?? 42);
		return Task.CompletedTask;
	}
}

using BenchmarkDotNet.Attributes;
using Hangfire;
using Hangfire.Common;
using Hangfire.MemoryStorage;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Quartz;
using Quartz.Impl;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Immediate.Jobs.Benchmarks;

[MemoryDiagnoser]
public class EnqueueBenchmarks : IAsyncDisposable
{
	private readonly TimeProvider _timeProvider = TimeProvider.System;
	private BenchmarkScheduler _immediate = null!;
	private InMemoryJobStorage? _immediateStorage;
	private IBackgroundJobClient _hangfire = null!;
	private IScheduler _quartz = null!;
	private long _sequence;

	[GlobalSetup]
	public async Task Setup()
	{
		_immediateStorage = new InMemoryJobStorage(_timeProvider);
		_immediate = new(
			_immediateStorage,
			new SystemTextJsonJobSerializer(),
			_timeProvider,
			BenchmarkIdGenerator.Instance
		);
		_ = GlobalConfiguration.Configuration.UseMemoryStorage();
		_hangfire = new BackgroundJobClient();
		_quartz = await new StdSchedulerFactory().GetScheduler();
		await _quartz.Start();
	}

	[GlobalCleanup]
	public async Task Cleanup()
	{
		await _quartz.Shutdown(waitForJobsToComplete: true);
		await DisposeAsync();
	}

	public async ValueTask DisposeAsync()
	{
		if (_immediateStorage is null)
			return;
		await _immediateStorage.DisposeAsync();
		_immediateStorage = null;
		GC.SuppressFinalize(this);
	}

	[Benchmark(Baseline = true)]
	public ValueTask<JobHandle> ImmediateJobs() => _immediate.EnqueueAsync(new(42));

	[Benchmark]
	public string Hangfire() => _hangfire.Enqueue(() => BenchmarkOperations.Execute(42));

	[Benchmark]
	public async Task<DateTimeOffset> Quartz()
	{
		var sequence = Interlocked.Increment(ref _sequence);
		var job = JobBuilder.Create<QuartzNoOpJob>()
			.WithIdentity(string.Create(CultureInfo.InvariantCulture, $"enqueue-job-{sequence}"))
			.UsingJobData("value", 42)
			.Build();
		var trigger = TriggerBuilder.Create()
			.WithIdentity(string.Create(CultureInfo.InvariantCulture, $"enqueue-trigger-{sequence}"))
			.StartNow()
			.Build();
		return await _quartz.ScheduleJob(job, trigger);
	}

	private sealed class BenchmarkScheduler(
		IJobStorage storage,
		IJobSerializer serializer,
		TimeProvider timeProvider,
		IIdGenerator idGenerator
	)
		: JobScheduler<BenchmarkPayload>(
			storage,
			serializer,
			timeProvider,
			idGenerator,
			"benchmark-job",
			JobQueueDefinition.DefaultName,
			static options => new BenchmarkJsonContext(options).BenchmarkPayload
		);
}

[MemoryDiagnoser]
public class StartupBenchmarks
{
	[Benchmark(Baseline = true)]
	[SuppressMessage(
		"Reliability",
		"CA2000",
		Justification = "The returned scheduler owns the benchmark-only in-memory storage."
	)]
	public object ImmediateJobs()
	{
		return new StartupScheduler(
			new InMemoryJobStorage(TimeProvider.System),
			new SystemTextJsonJobSerializer(),
			TimeProvider.System,
			BenchmarkIdGenerator.Instance
		);
	}

	[Benchmark]
	public object Hangfire()
	{
		var storage = new MemoryStorage();
		return new BackgroundJobClient(storage);
	}

	[Benchmark]
	public object Quartz() => new StdSchedulerFactory();

	[SuppressMessage(
		"Style",
		"IDE0290",
		Justification = "The explicit constructor makes transferred storage ownership unambiguous."
	)]
	private sealed class StartupScheduler : JobScheduler<BenchmarkPayload>, IAsyncDisposable
	{
		private readonly IJobStorage _storage;

		public StartupScheduler(
			IJobStorage storage,
			IJobSerializer serializer,
			TimeProvider timeProvider,
			IIdGenerator idGenerator
		) : base(
			storage,
			serializer,
			timeProvider,
			idGenerator,
			"benchmark-job",
			JobQueueDefinition.DefaultName,
			static options => new BenchmarkJsonContext(options).BenchmarkPayload
		)
		{
			_storage = storage;
		}

		public ValueTask DisposeAsync() => _storage.DisposeAsync();
	}
}

internal sealed class BenchmarkIdGenerator : IIdGenerator
{
	public static readonly BenchmarkIdGenerator Instance = new();

	public string CreateId(IdKind kind) => Guid.NewGuid().ToString("N");
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
			JobHandle = JobHandle.FromString(Guid.NewGuid().ToString("N")),
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

		_execution = new JobExecution
		{
			Record = record,
			Definition = definition,
			CancellationToken = CancellationToken.None,
		};
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

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests;

public sealed class StorageCapabilityTests
{
	[Fact]
	public async Task InMemoryStorageResolvesOneInstanceAndReportsItsCapabilities()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddImmediateJobsCore().ConfigureStorage(options => _ = options.UseInMemory());

		await using var provider = services.BuildServiceProvider();
		var storage = provider.GetRequiredService<IJobStorage>();

		Assert.IsType<IFairQueueStorage>(storage, exactMatch: false);
		Assert.IsType<IRecurringJobStorage>(storage, exactMatch: false);
		Assert.IsType<IJobGraphStorage>(storage, exactMatch: false);

		Assert.Equal(
			StorageCapabilities.Queue |
			StorageCapabilities.Recurring |
			StorageCapabilities.Graph |
			StorageCapabilities.FairQueues,
			(await storage.GetMonitoringSnapshotAsync(cancellationToken)).Capabilities
		);
	}

	[Fact]
	public async Task GraphEntryPointsFailBeforeWriting()
	{
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new QueueOnlyStorage(timeProvider);
		var idGenerator = new CapabilityIdGenerator();
		var scheduler = new QueueOnlyScheduler(
			storage,
			new SystemTextJsonJobSerializer(),
			timeProvider,
			idGenerator
		);
		var batchScheduler = new BatchScheduler(
			storage,
			timeProvider,
			idGenerator
		);

		var beginException = Assert.Throws<NotSupportedException>(batchScheduler.Begin);
		Assert.Contains("SQL database", beginException.Message, StringComparison.Ordinal);

		var continuationException = await Assert.ThrowsAsync<NotSupportedException>(() =>
			scheduler.ScheduleAfterAsync(
				new JobHandle("parent"),
				"payload",
				cancellationToken: TestContext.Current.CancellationToken
			).AsTask());
		Assert.Contains("SQL database", continuationException.Message, StringComparison.Ordinal);
		Assert.Equal(0, storage.EnqueueCalls);
	}

	[Fact]
	public async Task QueueOnlySchedulerUsesPlainCompletionAndSkipsRecurring()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new QueueOnlyStorage(timeProvider);
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddImmediateJobsCore()
			.Configure(o => o.MaxParallelJobs = 1)
			.ConfigureStorage(o => o.UseStorage(_ => storage).UseDistributed());

		_ = services.AddSingleton(new JobDefinition
		{
			Name = "queue-only",
			Cron = "* * * * *",
			JobType = typeof(StorageCapabilityTests),
			Invoker = new NoopInvoker(),
		});

		await using var provider = services.BuildServiceProvider();
		await storage.EnqueueAsync(new()
		{
			Id = "queue-only-job",
			JobName = "queue-only",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = DateTimeOffset.MinValue,
			CreatedAt = DateTimeOffset.MinValue,
		}, cancellationToken);

		await provider.GetRequiredService<JobSchedulingService>().DrainAsync(cancellationToken);

		Assert.Equal(1, storage.CompleteCalls);
		Assert.Equal(JobState.Succeeded, (await storage.GetJobStatusAsync("queue-only-job", cancellationToken))!.State);
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		Assert.Empty(snapshot.Recurring);
		Assert.Equal(StorageCapabilities.Queue, snapshot.Capabilities);
	}

	private sealed class QueueOnlyScheduler(
		IJobStorage storage,
		IJobSerializer serializer,
		TimeProvider timeProvider,
		IIdGenerator idGenerator
	) : JobScheduler<string>(
		storage,
		serializer,
		timeProvider,
		idGenerator,
		"queue-only",
		JobQueueDefinition.DefaultName,
		GetStringTypeInfo
	);

	private static JsonTypeInfo<string> GetStringTypeInfo(JsonSerializerOptions options)
	{
#if NET11_0_OR_GREATER
		return options.GetTypeInfo<string>();
#else
		return (JsonTypeInfo<string>)options.GetTypeInfo(typeof(string));
#endif
	}

	private sealed class NoopInvoker : IJobInvoker
	{
		public ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution) =>
			ValueTask.CompletedTask;
	}

	private sealed class CapabilityIdGenerator : IIdGenerator
	{
		private int _value;
		public string CreateId(IdKind kind) => string.Create(CultureInfo.InvariantCulture, $"{kind}-{Interlocked.Increment(ref _value)}");
	}

	internal sealed class QueueOnlyStorage(TimeProvider timeProvider) : IJobStorage
	{
		private readonly InMemoryJobStorage _inner = new(timeProvider);

		public int CompleteCalls { get; private set; }
		public int EnqueueCalls { get; private set; }

		public ValueTask DisposeAsync() => _inner.DisposeAsync();

		public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
			_inner.InitializeAsync(cancellationToken);

		public ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
		{
			EnqueueCalls++;
			return _inner.EnqueueAsync(job, cancellationToken);
		}

		public ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
			JobAcquisitionRequest request,
			CancellationToken cancellationToken = default
		) => _inner.AcquireDueJobsAsync(request, cancellationToken);

		public ValueTask SetExecutionTelemetryAsync(
			string jobId,
			int executionNumber,
			string workerId,
			string? traceId,
			string? spanId,
			DateTimeOffset startedAt,
			CancellationToken cancellationToken = default
		) => _inner.SetExecutionTelemetryAsync(
			jobId,
			executionNumber,
			workerId,
			traceId,
			spanId,
			startedAt,
			cancellationToken
		);

		public ValueTask RenewLeaseAsync(
			string jobId,
			int executionNumber,
			string workerId,
			TimeSpan lease,
			CancellationToken cancellationToken = default
		) => _inner.RenewLeaseAsync(jobId, executionNumber, workerId, lease, cancellationToken);

		public ValueTask CompleteAsync(
			string jobId,
			int executionNumber,
			string workerId,
			CancellationToken cancellationToken = default
		)
		{
			CompleteCalls++;
			return _inner.CompleteAsync(jobId, executionNumber, workerId, cancellationToken);
		}

		public ValueTask FailAsync(
			string jobId,
			int executionNumber,
			string workerId,
			string error,
			DateTimeOffset? nextRetryAt,
			CancellationToken cancellationToken = default
		) => _inner.FailAsync(jobId, executionNumber, workerId, error, nextRetryAt, cancellationToken);

		public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(
			CancellationToken cancellationToken = default
		)
		{
			var snapshot = await _inner.GetMonitoringSnapshotAsync(cancellationToken);
			return snapshot with
			{
				Recurring = [],
				Capabilities = this.GetCapabilities(),
			};
		}

		public ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
			JobQuery query,
			CancellationToken cancellationToken = default
		) => _inner.QueryJobsAsync(query, cancellationToken);

		public ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
			JobExecutionQuery query,
			CancellationToken cancellationToken = default
		) => _inner.QueryJobExecutionsAsync(query, cancellationToken);

		public ValueTask<JobStatus?> GetJobStatusAsync(
			string jobId,
			CancellationToken cancellationToken = default
		) => _inner.GetJobStatusAsync(jobId, cancellationToken);

		public ValueTask CancelAsync(string jobId, CancellationToken cancellationToken = default) =>
			_inner.CancelAsync(jobId, cancellationToken);

		public ValueTask RetryAsync(string jobId, CancellationToken cancellationToken = default) =>
			_inner.RetryAsync(jobId, cancellationToken);

		public ValueTask DeleteAsync(string jobId, CancellationToken cancellationToken = default) =>
			_inner.DeleteAsync(jobId, cancellationToken);

		public ValueTask PurgeJobsAsync(
			TimeSpan succeededRetention,
			TimeSpan failedRetention,
			CancellationToken cancellationToken = default
		) => _inner.PurgeJobsAsync(succeededRetention, failedRetention, cancellationToken);

		public ValueTask HeartbeatAsync(
			JobServerSnapshot server,
			CancellationToken cancellationToken = default
		) => _inner.HeartbeatAsync(server, cancellationToken);

		public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default) =>
			_inner.IsHealthyAsync(cancellationToken);
	}
}

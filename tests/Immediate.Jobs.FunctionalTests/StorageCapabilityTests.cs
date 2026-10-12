using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
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

		Assert.IsType<IJobGraphStorage>(storage, exactMatch: false);

		Assert.Equal(
			StorageCapabilities.Queue |
			StorageCapabilities.Graph,
			(await storage.GetMonitoringSnapshotAsync(cancellationToken)).Capabilities
		);
	}

	[Fact]
	public async Task GraphEntryPointsFailBeforeWriting()
	{
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new QueueOnlyStorage(timeProvider);
		var idGenerator = new CapabilityIdGenerator();
		var scheduler = new PlainRequestJob.Scheduler(
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
				new("payload"),
				JobHandle.FromString("parent"),
				cancellationToken: TestContext.Current.CancellationToken
			).AsTask());
		Assert.Contains("SQL database", continuationException.Message, StringComparison.Ordinal);
		Assert.Equal(0, storage.EnqueueCalls);
	}

	private sealed class CapabilityIdGenerator : IIdGenerator
	{
		private int _value;
		public string CreateId(IdKind kind) => string.Create(CultureInfo.InvariantCulture, $"{kind}-{Interlocked.Increment(ref _value)}");
	}

	internal sealed class QueueOnlyStorage(TimeProvider timeProvider) : IJobStorage
	{
		private readonly InMemoryJobStorage _inner = new(timeProvider);

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
			JobHandle jobHandle,
			int executionNumber,
			string workerId,
			string? traceId,
			string? spanId,
			DateTimeOffset startedAt,
			CancellationToken cancellationToken = default
		) => _inner.SetExecutionTelemetryAsync(
			jobHandle,
			executionNumber,
			workerId,
			traceId,
			spanId,
			startedAt,
			cancellationToken
		);

		public ValueTask RenewLeaseAsync(
			JobHandle jobHandle,
			int executionNumber,
			string workerId,
			TimeSpan lease,
			CancellationToken cancellationToken = default
		) => _inner.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);

		public ValueTask CompleteAsync(
			JobHandle jobHandle,
			int executionNumber,
			string workerId,
			CancellationToken cancellationToken = default
		) => _inner.CompleteAsync(jobHandle, executionNumber, workerId, cancellationToken);

		public ValueTask FailAsync(
			JobHandle jobHandle,
			int executionNumber,
			string workerId,
			string error,
			DateTimeOffset? nextRetryAt,
			CancellationToken cancellationToken = default
		) => _inner.FailAsync(jobHandle, executionNumber, workerId, error, nextRetryAt, cancellationToken);

		public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(
			CancellationToken cancellationToken = default
		)
		{
			var snapshot = await _inner.GetMonitoringSnapshotAsync(cancellationToken);
			return snapshot with
			{
				Capabilities = this.GetCapabilities(),
			};
		}

		public ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
			JobQuery query,
			CancellationToken cancellationToken = default
		) => _inner.QueryJobsAsync(query, cancellationToken);

		public ValueTask<IReadOnlyList<JobRecord>> QueryNonCompletedJobsAsync(
			string jobName,
			CancellationToken cancellationToken = default
		) => _inner.QueryNonCompletedJobsAsync(jobName, cancellationToken);

		public ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
			JobHandle jobHandle,
			JobExecutionQuery query,
			CancellationToken cancellationToken = default
		) => _inner.QueryJobExecutionsAsync(jobHandle, query, cancellationToken);

		public ValueTask<JobStatus?> GetJobStatusAsync(
			JobHandle jobHandle,
			CancellationToken cancellationToken = default
		) => _inner.GetJobStatusAsync(jobHandle, cancellationToken);

		public ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) =>
			_inner.CancelAsync(jobHandle, cancellationToken);

		public ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) =>
			_inner.RetryAsync(jobHandle, cancellationToken);

		public ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) =>
			_inner.DeleteAsync(jobHandle, cancellationToken);

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

		public ValueTask UpdatePayloadAsync(
			JobHandle jobHandle,
			string expectedJobName,
			string payload,
			CancellationToken cancellationToken = default
		) => _inner.UpdatePayloadAsync(jobHandle, expectedJobName, payload, cancellationToken);

		public ValueTask<bool> TryTriggerAsync(
			JobHandle jobHandle,
			string expectedJobName,
			DateTimeOffset dueAt,
			CancellationToken cancellationToken = default
		) => _inner.TryTriggerAsync(jobHandle, expectedJobName, dueAt, cancellationToken);

		public ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default) =>
			_inner.UpsertRecurringAsync(schedule, cancellationToken);

		public ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default) =>
			_inner.RemoveRecurringAsync(name, cancellationToken);

		public ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default) =>
			_inner.PauseRecurringAsync(name, cancellationToken);

		public ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default) =>
			_inner.ResumeRecurringAsync(name, cancellationToken);

		public ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default) =>
			_inner.MergeJobDefinitionsListAsync(registration, cancellationToken);

		public ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default) =>
			_inner.GetJobDefinitionsAsync(cancellationToken);

		public ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
			DateTimeOffset now,
			int batchSize,
			CancellationToken cancellationToken = default
		) => _inner.GetDueRecurringAsync(now, batchSize, cancellationToken);

		public ValueTask<bool> MaterializeRecurringAsync(
			RecurringJobSchedule schedule,
			JobRecord job,
			DateTimeOffset nextRunAt,
			IReadOnlyList<JobContinuationEdge>? dependencies = null,
			CancellationToken cancellationToken = default
		) => _inner.MaterializeRecurringAsync(schedule, job, nextRunAt, dependencies, cancellationToken);
	}
}

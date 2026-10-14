using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

#pragma warning disable IDE0130
namespace Immediate.Jobs.Testing;

internal static partial class ReplicaStorageConformance
{
	private const string EnqueueRaceName = "Replica.Acquisition.WaitsUntilEnqueueHasUpdatedBothStores";

	private static async ValueTask AcquisitionWaitsUntilEnqueueHasUpdatedBothStoresAsync(
		IJobStorage storage,
		FakeTimeProvider clock,
		CancellationToken token
	)
	{
		await using var durable = new DelayedEnqueueStorage(Replica(storage, EnqueueRaceName));
		var services = new ServiceCollection();
		services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
		services.AddSingleton<TimeProvider>(clock);
		services.AddImmediateJobsCore().ConfigureStorage(builder => builder.UseSingleServer(_ => durable));

		await using var provider = services.BuildServiceProvider();
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(token);
		await writer.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = JobName, QueueName = QueueName }],
		}, token);

		var job = Job("enqueue-race", clock.GetUtcNow());
		var enqueue = writer.EnqueueAsync(job, token).AsTask();
		Task<IReadOnlyList<JobRecord>>? acquisition = null;
		try
		{
			await durable.EnqueueCommitted.Task.WaitAsync(token);

			// On the default scheduler, the initialized wrapper runs synchronously until its
			// first incomplete await. Await only the invocation, leaving acquisition pending.
			acquisition = (await Task.Run(
				() => writer.AcquireDueJobsAsync(new()
				{
					WorkerId = "enqueue-race-worker",
					Lease = TimeSpan.FromMinutes(1),
					BatchSize = 1,
					Queues =
					[
						new()
						{
							QueueName = QueueName,
							Capacity = 1,
							JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [JobName] = 1 },
						},
					],
				}, token),
				token
			)).AsTask();

			ConformanceAssert.False(durable.AcquisitionEntered.Task.IsCompleted, EnqueueRaceName,
				"durable acquisition must wait until enqueue has updated the in-memory store");
			ConformanceAssert.False(acquisition.IsCompleted, EnqueueRaceName,
				"acquisition must remain pending while enqueue is between its durable and in-memory writes");
		}
		finally
		{
			durable.ReleaseEnqueue.TrySetResult();
			if (acquisition is not null)
				await Task.WhenAll(enqueue, acquisition);
			else
				await enqueue;
		}

		var active = ConformanceAssert.NotNull((await acquisition).SingleOrDefault(), EnqueueRaceName,
			"acquisition must return the job once enqueue has updated both stores");
		ConformanceAssert.Equal(job.JobHandle, active.JobHandle, EnqueueRaceName, "acquisition must claim the enqueued job");
		ConformanceAssert.Equal(JobState.Active, (await GetJobAsync(writer, job.JobHandle, EnqueueRaceName, token)).State,
			EnqueueRaceName, "the in-memory store must retain the acquired ownership");
		ConformanceAssert.Equal(JobState.Active, (await GetJobAsync(storage, job.JobHandle, EnqueueRaceName, token)).State,
			EnqueueRaceName, "the durable store must retain the acquired ownership");

		await writer.CompleteAsync(job.JobHandle, active.Attempt, "enqueue-race-worker", token);
		ConformanceAssert.Equal(JobState.Succeeded, (await GetJobAsync(writer, job.JobHandle, EnqueueRaceName, token)).State,
			EnqueueRaceName, "the in-memory store must allow completion of the acquired job");
		ConformanceAssert.Equal(JobState.Succeeded, (await GetJobAsync(storage, job.JobHandle, EnqueueRaceName, token)).State,
			EnqueueRaceName, "completion must also update the durable store");
	}

	// Forward every operation to the fixture's provider, pausing only after its enqueue commits.
	private sealed class DelayedEnqueueStorage(IJobGraphStorage storage) : IJobGraphStorage
	{
		public TaskCompletionSource EnqueueCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseEnqueue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AcquisitionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <inheritdoc />
		public async ValueTask EnqueueAsync(JobSubmission job, CancellationToken cancellationToken = default)
		{
			await storage.EnqueueAsync(job, cancellationToken);
			EnqueueCommitted.SetResult();
			await ReleaseEnqueue.Task.WaitAsync(cancellationToken);
		}

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(JobAcquisitionRequest request, CancellationToken cancellationToken = default)
		{
			AcquisitionEntered.SetResult();
			return storage.AcquireDueJobsAsync(request, cancellationToken);
		}

		/// <inheritdoc />
		public ValueTask DisposeAsync() => storage.DisposeAsync();

		/// <inheritdoc />
		public ValueTask PauseJobAsync(string jobName, CancellationToken cancellationToken = default) =>
			storage.PauseJobAsync(jobName, cancellationToken);

		/// <inheritdoc />
		public ValueTask ResumeJobAsync(string jobName, CancellationToken cancellationToken = default) =>
			storage.ResumeJobAsync(jobName, cancellationToken);

		/// <inheritdoc />
		public ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, CancellationToken cancellationToken = default) =>
			storage.GetJobAcquisitionStateAsync(jobName, cancellationToken);

		/// <inheritdoc />
		public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
			storage.InitializeAsync(cancellationToken);

		/// <inheritdoc />
		public ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default) =>
			storage.MergeJobDefinitionsListAsync(registration, cancellationToken);

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default) =>
			storage.GetJobDefinitionsAsync(cancellationToken);

		/// <inheritdoc />
		public ValueTask UpdatePayloadAsync(JobHandle jobHandle, string expectedJobName, string payload, CancellationToken cancellationToken = default) =>
			storage.UpdatePayloadAsync(jobHandle, expectedJobName, payload, cancellationToken);

		/// <inheritdoc />
		public ValueTask<bool> TryTriggerAsync(JobHandle jobHandle, string expectedJobName, DateTimeOffset dueAt, CancellationToken cancellationToken = default) =>
			storage.TryTriggerAsync(jobHandle, expectedJobName, dueAt, cancellationToken);

		/// <inheritdoc />
		public ValueTask SetExecutionTelemetryAsync(JobHandle jobHandle, int executionNumber, string workerId, string? traceId, string? spanId, DateTimeOffset startedAt, CancellationToken cancellationToken = default) =>
			storage.SetExecutionTelemetryAsync(jobHandle, executionNumber, workerId, traceId, spanId, startedAt, cancellationToken);

		/// <inheritdoc />
		public ValueTask RenewLeaseAsync(JobHandle jobHandle, int executionNumber, string workerId, TimeSpan lease, CancellationToken cancellationToken = default) =>
			storage.RenewLeaseAsync(jobHandle, executionNumber, workerId, lease, cancellationToken);

		/// <inheritdoc />
		public ValueTask CompleteAsync(JobHandle jobHandle, int executionNumber, string workerId, CancellationToken cancellationToken = default) =>
			storage.CompleteAsync(jobHandle, executionNumber, workerId, cancellationToken);

		/// <inheritdoc />
		public ValueTask FailAsync(JobHandle jobHandle, int executionNumber, string workerId, string error, DateTimeOffset? nextRetryAt, CancellationToken cancellationToken = default) =>
			storage.FailAsync(jobHandle, executionNumber, workerId, error, nextRetryAt, cancellationToken);

		/// <inheritdoc />
		public ValueTask<JobMonitoringDefinitions> GetMonitoringDefinitionsAsync(CancellationToken cancellationToken = default) =>
			storage.GetMonitoringDefinitionsAsync(cancellationToken);

		/// <inheritdoc />
		public ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default) =>
			storage.GetMonitoringSnapshotAsync(cancellationToken);

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default) =>
			storage.QueryJobsAsync(query, cancellationToken);

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<JobRecord>> QueryNonCompletedJobsAsync(string jobName, CancellationToken cancellationToken = default) =>
			storage.QueryNonCompletedJobsAsync(jobName, cancellationToken);

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(JobHandle jobHandle, JobExecutionQuery query, CancellationToken cancellationToken = default) =>
			storage.QueryJobExecutionsAsync(jobHandle, query, cancellationToken);

		/// <inheritdoc />
		public ValueTask<JobStatus?> GetJobStatusAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) =>
			storage.GetJobStatusAsync(jobHandle, cancellationToken);

		/// <inheritdoc />
		public ValueTask CancelAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) =>
			storage.CancelAsync(jobHandle, cancellationToken);

		/// <inheritdoc />
		public ValueTask RetryAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) =>
			storage.RetryAsync(jobHandle, cancellationToken);

		/// <inheritdoc />
		public ValueTask DeleteAsync(JobHandle jobHandle, CancellationToken cancellationToken = default) =>
			storage.DeleteAsync(jobHandle, cancellationToken);

		/// <inheritdoc />
		public ValueTask PurgeJobsAsync(TimeSpan succeededRetention, TimeSpan failedRetention, CancellationToken cancellationToken = default) =>
			storage.PurgeJobsAsync(succeededRetention, failedRetention, cancellationToken);

		/// <inheritdoc />
		public ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default) =>
			storage.HeartbeatAsync(server, cancellationToken);

		/// <inheritdoc />
		public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default) =>
			storage.IsHealthyAsync(cancellationToken);

		/// <inheritdoc />
		public ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default) =>
			storage.UpsertRecurringAsync(schedule, cancellationToken);

		/// <inheritdoc />
		public ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default) =>
			storage.RemoveRecurringAsync(name, cancellationToken);

		/// <inheritdoc />
		public ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default) =>
			storage.PauseRecurringAsync(name, cancellationToken);

		/// <inheritdoc />
		public ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default) =>
			storage.ResumeRecurringAsync(name, cancellationToken);

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(DateTimeOffset now, int batchSize, IReadOnlyList<string> jobNames, CancellationToken cancellationToken = default) =>
			storage.GetDueRecurringAsync(now, batchSize, jobNames, cancellationToken);

		/// <inheritdoc />
		public ValueTask<bool> MaterializeRecurringAsync(RecurringJobSchedule schedule, JobRecord job, DateTimeOffset nextRunAt, IReadOnlyList<JobContinuationEdge>? dependencies = null, CancellationToken cancellationToken = default) =>
			storage.MaterializeRecurringAsync(schedule, job, nextRunAt, dependencies, cancellationToken);

		/// <inheritdoc />
		public ValueTask EnqueueContinuationAsync(JobSubmission job, IReadOnlyList<JobContinuationEdge> edges, CancellationToken cancellationToken = default) =>
			storage.EnqueueContinuationAsync(job, edges, cancellationToken);

		/// <inheritdoc />
		public ValueTask EnqueueBatchAsync(BatchRecord batch, IReadOnlyList<JobSubmission> jobs, IReadOnlyList<JobContinuationEdge> edges, CancellationToken cancellationToken = default) =>
			storage.EnqueueBatchAsync(batch, jobs, edges, cancellationToken);

		/// <inheritdoc />
		public ValueTask CompleteWithContinuationsAsync(JobHandle jobHandle, int executionNumber, string workerId, IReadOnlyList<JobContinuationAddition> additions, CancellationToken cancellationToken = default) =>
			storage.CompleteWithContinuationsAsync(jobHandle, executionNumber, workerId, additions, cancellationToken);

		/// <inheritdoc />
		public ValueTask AddBatchJobAsync(JobHandle currentJobHandle, int executionNumber, JobSubmission job, ContinuationOptions options, CancellationToken cancellationToken = default) =>
			storage.AddBatchJobAsync(currentJobHandle, executionNumber, job, options, cancellationToken);

		/// <inheritdoc />
		public ValueTask<BatchStatus?> GetBatchStatusAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default) =>
			storage.GetBatchStatusAsync(batchHandle, cancellationToken);

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(BatchQuery query, CancellationToken cancellationToken = default) =>
			storage.QueryBatchesAsync(query, cancellationToken);

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(BatchHandle batchHandle, BatchMemberQuery query, CancellationToken cancellationToken = default) =>
			storage.QueryBatchMembersAsync(batchHandle, query, cancellationToken);

		/// <inheritdoc />
		public ValueTask<BatchGraph?> GetBatchGraphAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default) =>
			storage.GetBatchGraphAsync(batchHandle, cancellationToken);

		/// <inheritdoc />
		public ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default) =>
			storage.CancelBatchAsync(batchHandle, cancellationToken);

		/// <inheritdoc />
		public ValueTask<bool> TryTriggerBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default) =>
			storage.TryTriggerBatchAsync(batchHandle, cancellationToken);

		/// <inheritdoc />
		public ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default) =>
			storage.DeleteBatchAsync(batchHandle, cancellationToken);

		/// <inheritdoc />
		public ValueTask PurgeBatchesAsync(TimeSpan batchSucceededRetention, TimeSpan batchFailedRetention, CancellationToken cancellationToken = default) =>
			storage.PurgeBatchesAsync(batchSucceededRetention, batchFailedRetention, cancellationToken);

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(IReadOnlyCollection<JobHandle> jobs, string workerId, TimeSpan lease, CancellationToken cancellationToken = default) =>
			storage.AcquireJobsAsync(jobs, workerId, lease, cancellationToken);

		/// <inheritdoc />
		public ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(IReadOnlyCollection<JobHandle> childJobs, CancellationToken cancellationToken = default) =>
			storage.GetIncomingEdgesAsync(childJobs, cancellationToken);
	}
}

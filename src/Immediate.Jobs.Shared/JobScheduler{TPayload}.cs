using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Runtime base used by source-generated typed schedulers.
/// </summary>
/// <typeparam name="TPayload">
/// 	The job payload type.
/// </typeparam>
/// <param name="storage">
/// 	The storage provider used to persist invocations and schedules.
/// </param>
/// <param name="serializer">
/// 	The serializer used for payload and context data.
/// </param>
/// <param name="timeProvider">
/// 	The clock used to determine due times.
/// </param>
/// <param name="idGenerator">
/// 	The generator used to create invocation identifiers.
/// </param>
/// <param name="jobName">
/// 	The stable generated job name.
/// </param>
/// <param name="queueName">
/// 	The queue used for new invocations.
/// </param>
/// <param name="payloadTypeInfoFactory">
/// 	The factory that supplies JSON metadata for the payload type.
/// </param>
public abstract class JobScheduler<TPayload>(
	IJobStorage storage,
	IJobSerializer serializer,
	TimeProvider timeProvider,
	IIdGenerator idGenerator,
	string jobName,
	string queueName,
	Func<System.Text.Json.JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
) : IJobScheduler<TPayload>
{
	/// <summary>
	/// 	The storage provider.
	/// </summary>
	/// <value>
	/// 	The provider used to persist jobs and schedules.
	/// </value>
	protected IJobStorage Storage { get; } = storage;

	/// <summary>
	/// 	The serializer used with generated payload and context metadata.
	/// </summary>
	/// <value>
	/// 	The serializer used by this scheduler.
	/// </value>
	protected IJobSerializer Serializer { get; } = serializer;

	/// <summary>
	/// 	The clock used for deterministic scheduling.
	/// </summary>
	/// <value>
	/// 	The clock used to determine due times.
	/// </value>
	protected TimeProvider TimeProvider { get; } = timeProvider;

	/// <summary>
	/// 	The stable generated name.
	/// </summary>
	public string JobName { get; } = jobName;

	/// <summary>
	/// 	The stable queue used for new invocations.
	/// </summary>
	public string QueueName { get; } = queueName;

	/// <summary>
	/// 	Captures the context envelope persisted with a new invocation.
	/// </summary>
	/// <returns>
	/// 	The serialized context envelope, or <see langword="null"/> when there is no context.
	/// </returns>
	protected virtual string? CaptureContext()
	{
		return null;
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> EnqueueAsync(TPayload payload, CancellationToken cancellationToken = default)
	{
		return await ScheduleJobAsync(payload, TimeSpan.Zero, groupId: null, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> EnqueueAsync(TPayload payload, string groupId, CancellationToken cancellationToken = default)
	{
		return await ScheduleJobAsync(payload, TimeSpan.Zero, groupId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> EnqueueAsync(
		TPayload payload,
		JobDetails currentJob,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		return await AddCurrentBatchJobAsync(payload, currentJob, TimeSpan.Zero, groupId: null, options, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> EnqueueAsync(
		TPayload payload,
		JobDetails currentJob,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		return await AddCurrentBatchJobAsync(payload, currentJob, TimeSpan.Zero, groupId, options, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAsync(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default)
	{
		return await ScheduleJobAsync(payload, delay, groupId: null, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	)
	{
		return await ScheduleJobAsync(payload, delay, groupId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		return await AddCurrentBatchJobAsync(payload, currentJob, delay, groupId: null, options, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		return await AddCurrentBatchJobAsync(payload, currentJob, delay, groupId, options, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAsync(TPayload payload, DateTimeOffset at, CancellationToken cancellationToken = default)
	{
		return await ScheduleJobAsync(payload, at, groupId: null, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		DateTimeOffset at,
		string groupId,
		CancellationToken cancellationToken = default
	)
	{
		return await ScheduleJobAsync(payload, at, groupId, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		JobDetails currentJob,
		DateTimeOffset at,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		return await AddCurrentBatchJobAsync(payload, currentJob, at, groupId: null, options, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		JobDetails currentJob,
		DateTimeOffset at,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		return await AddCurrentBatchJobAsync(payload, currentJob, at, groupId, options, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		ContinuationHandle parent,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parent);
		return await ScheduleAfterCoreAsync(payload, TimeSpan.Zero, groupId: null, parents: [parent], on: on, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		ContinuationHandle parent,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parent);
		return await ScheduleAfterCoreAsync(payload, TimeSpan.Zero, groupId, [parent], on, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		ContinuationHandle parent,
		TimeSpan delay,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parent);
		return await ScheduleAfterCoreAsync(payload, delay, groupId: null, parents: [parent], on: on, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		ContinuationHandle parent,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parent);
		return await ScheduleAfterCoreAsync(payload, delay, groupId, [parent], on, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<ContinuationHandle> parents,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parents);
		return await ScheduleAfterCoreAsync(payload, TimeSpan.Zero, groupId: null, parents, on, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<ContinuationHandle> parents,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parents);
		return await ScheduleAfterCoreAsync(payload, TimeSpan.Zero, groupId, parents, on, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<ContinuationHandle> parents,
		TimeSpan delay,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parents);
		return await ScheduleAfterCoreAsync(payload, delay, groupId: null, parents, on, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<ContinuationHandle> parents,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parents);
		return await ScheduleAfterCoreAsync(payload, delay, groupId, parents, on, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	)
	{
		return ScheduleAfterCurrentJobCore(payload, currentJob, TimeSpan.Zero, groupId: null, options);
	}

	/// <inheritdoc />
	public JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	)
	{
		return ScheduleAfterCurrentJobCore(payload, currentJob, TimeSpan.Zero, groupId, options);
	}

	/// <inheritdoc />
	public JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	)
	{
		return ScheduleAfterCurrentJobCore(payload, currentJob, delay, groupId: null, options);
	}

	/// <inheritdoc />
	public JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	)
	{
		return ScheduleAfterCurrentJobCore(payload, currentJob, delay, groupId, options);
	}

	/// <inheritdoc />
	public BatchJobHandle Enqueue(TPayload payload, Batch batch)
	{
		return ScheduleBatchJob(payload, batch, TimeSpan.Zero, groupId: null);
	}

	/// <inheritdoc />
	public BatchJobHandle Enqueue(TPayload payload, Batch batch, string groupId)
	{
		return ScheduleBatchJob(payload, batch, TimeSpan.Zero, groupId);
	}

	/// <inheritdoc />
	public BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay)
	{
		return ScheduleBatchJob(payload, batch, delay, groupId: null);
	}

	/// <inheritdoc />
	public BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay, string groupId)
	{
		return ScheduleBatchJob(payload, batch, delay, groupId);
	}

	/// <inheritdoc />
	public BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at)
	{
		return ScheduleBatchJob(payload, batch, at, groupId: null);
	}

	/// <inheritdoc />
	public BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at, string groupId)
	{
		return ScheduleBatchJob(payload, batch, at, groupId);
	}

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		return ScheduleAfterBatchJobCore(payload, TimeSpan.Zero, groupId: null, [job], on);
	}

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		return ScheduleAfterBatchJobCore(payload, TimeSpan.Zero, groupId, [job], on);
	}

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		TimeSpan delay,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		return ScheduleAfterBatchJobCore(payload, delay, groupId: null, [job], on);
	}

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		return ScheduleAfterBatchJobCore(payload, delay, groupId, [job], on);
	}

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		return ScheduleAfterBatchJobCore(payload, TimeSpan.Zero, groupId: null, jobs, on);
	}

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		return ScheduleAfterBatchJobCore(payload, TimeSpan.Zero, groupId, jobs, on);
	}

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		TimeSpan delay,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		return ScheduleAfterBatchJobCore(payload, delay, groupId: null, jobs, on);
	}

	/// <inheritdoc />
	public BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		return ScheduleAfterBatchJobCore(payload, delay, groupId, jobs, on);
	}

	/// <inheritdoc />
	public async ValueTask CancelAsync(JobHandle job, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(job);

		await Storage.CancelAsync(job, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<JobHandle> ScheduleAfterCoreAsync(
		TPayload payload,
		TimeSpan delay,
		string? groupId,
		IReadOnlyList<ContinuationHandle> parents,
		ContinuationTrigger on,
		CancellationToken cancellationToken
	)
	{
		var graphStorage = JobStorageCapabilityGuards.RequireGraph(Storage);

		if (delay < TimeSpan.Zero)
			ArgumentOutOfRangeException.Throw(nameof(delay), $"A job delay cannot be negative. (delay: {delay:c})");

		if (parents is [])
			ArgumentException.Throw(nameof(parents), "No prior jobs or batches were provided");

		var parentIds = new HashSet<ContinuationHandle>();
		foreach (var parent in parents)
		{
			if (!parentIds.Add(parent))
				ImmediateJobException.Throw($"Duplicate continuation parent '{parent}'.");
		}

		var now = TimeProvider.GetUtcNow();
		var waiting = CreateRecord(payload, JobState.AwaitingContinuation, runAt: now + delay, now, groupId) with { RemainingDependencies = parents.Count };

		var edges = parents
			.Select(parent => new JobContinuationEdge
			{
				ChildJobId = waiting.JobId,
				ParentJobId = parent as JobHandle,
				ParentBatchId = parent as BatchHandle,
				Trigger = on,
				Delay = delay,
			});

		await graphStorage.EnqueueContinuationAsync(waiting, [.. edges], cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return waiting.JobId;
	}

	private async ValueTask<JobHandle> ScheduleJobAsync(
		TPayload payload,
		TimeSpan delay,
		string? groupId,
		CancellationToken cancellationToken
	)
	{
		if (delay < TimeSpan.Zero)
			ArgumentOutOfRangeException.Throw(nameof(delay), $"A job delay cannot be negative. (delay: {delay:c})");

		var now = TimeProvider.GetUtcNow();
		return await ScheduleJobCoreAsync(payload, now + delay, now, groupId, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<JobHandle> ScheduleJobAsync(
		TPayload payload,
		DateTimeOffset at,
		string? groupId,
		CancellationToken cancellationToken
	)
	{
		var now = TimeProvider.GetUtcNow();
		if (at < now)
			ArgumentOutOfRangeException.Throw(nameof(at), $"A job cannot be scheduled in the past (at: {at:O}, now: {now:O}).");

		return await ScheduleJobCoreAsync(payload, at, now, groupId, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<JobHandle> ScheduleJobCoreAsync(
		TPayload payload,
		DateTimeOffset runAt,
		DateTimeOffset now,
		string? groupId,
		CancellationToken cancellationToken
	)
	{
		var state = runAt == now ? JobState.Pending : JobState.Scheduled;
		var record = CreateRecord(payload, state, runAt, now, groupId);

		await Storage.EnqueueAsync(record, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return record.JobId;
	}

	private BatchJobHandle ScheduleBatchJob(
		TPayload payload,
		Batch batch,
		TimeSpan delay,
		string? groupId
	)
	{
		if (delay < TimeSpan.Zero)
			ArgumentOutOfRangeException.Throw(nameof(delay), $"A job delay cannot be negative. (delay: {delay:c})");

		var now = TimeProvider.GetUtcNow();
		return ScheduleBatchJobCore(payload, batch, now + delay, now, groupId);
	}

	private BatchJobHandle ScheduleBatchJob(
		TPayload payload,
		Batch batch,
		DateTimeOffset at,
		string? groupId
	)
	{
		var now = TimeProvider.GetUtcNow();
		if (at < now)
			ArgumentOutOfRangeException.Throw(nameof(at), $"A job cannot be scheduled in the past (at: {at:O}, now: {now:O}).");

		return ScheduleBatchJobCore(payload, batch, at, now, groupId);
	}

	private BatchJobHandle ScheduleBatchJobCore(
		TPayload payload,
		Batch batch,
		DateTimeOffset runAt,
		DateTimeOffset now,
		string? groupId
	)
	{
		ArgumentNullException.ThrowIfNull(batch);

		var state = runAt == now ? JobState.Pending : JobState.Scheduled;
		var record = CreateRecord(payload, state, runAt, now, groupId);

		return batch.Add(record);
	}

	private BatchJobHandle ScheduleAfterBatchJobCore(
		TPayload payload,
		TimeSpan delay,
		string? groupId,
		IReadOnlyList<BatchJobHandle> jobs,
		ContinuationTrigger on
	)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		if (jobs is [])
			ArgumentException.Throw(nameof(jobs), "No prior jobs were provided");

		if (delay < TimeSpan.Zero)
			ArgumentOutOfRangeException.Throw(nameof(delay), $"A job delay cannot be negative. (delay: {delay:c})");

		var now = TimeProvider.GetUtcNow();
		var record = CreateRecord(payload, JobState.AwaitingContinuation, runAt: now + delay, now, groupId);
		return jobs[0].Batch.Add(record, jobs, on, delay);
	}

	private async ValueTask<JobHandle> AddCurrentBatchJobAsync(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		string? groupId,
		ContinuationOptions options,
		CancellationToken cancellationToken
	)
	{
		if (delay < TimeSpan.Zero)
			ArgumentOutOfRangeException.Throw(nameof(delay), $"A job delay cannot be negative. (delay: {delay:c})");

		var now = TimeProvider.GetUtcNow();
		return await AddCurrentBatchJobCoreAsync(payload, currentJob, now + delay, now, groupId, options, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<JobHandle> AddCurrentBatchJobAsync(
		TPayload payload,
		JobDetails currentJob,
		DateTimeOffset at,
		string? groupId,
		ContinuationOptions options,
		CancellationToken cancellationToken
	)
	{
		var now = TimeProvider.GetUtcNow();
		if (at < now)
			ArgumentOutOfRangeException.Throw(nameof(at), $"A job cannot be scheduled in the past (at: {at:O}, now: {now:O}).");

		return await AddCurrentBatchJobCoreAsync(payload, currentJob, at, now, groupId, options, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<JobHandle> AddCurrentBatchJobCoreAsync(
		TPayload payload,
		JobDetails currentJob,
		DateTimeOffset runAt,
		DateTimeOffset now,
		string? groupId,
		ContinuationOptions options,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(currentJob);
		if (currentJob.BatchId is null)
			ArgumentException.Throw(nameof(currentJob), "The current job does not belong to a batch.");
		if (options == ContinuationOptions.Detached)
			ArgumentException.Throw(nameof(options), "A job added to the current batch cannot be detached.");

		var graphStorage = JobStorageCapabilityGuards.RequireGraph(Storage);
		var state = runAt == now ? JobState.Pending : JobState.Scheduled;
		var record = CreateRecord(payload, state, runAt, now, groupId) with { BatchId = currentJob.BatchId };

		await graphStorage.AddBatchJobAsync(currentJob.JobId, currentJob.Attempt, record, options, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return record.JobId;
	}

	private JobHandle ScheduleAfterCurrentJobCore(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		string? groupId,
		ContinuationOptions options
	)
	{
		ArgumentNullException.ThrowIfNull(currentJob);
		if (currentJob.Buffer is null)
			ArgumentException.Throw(nameof(currentJob), "JobDetails can schedule work only during its active execution attempt.");
		if (options != ContinuationOptions.Detached && currentJob.BatchId is null)
			ArgumentException.Throw(nameof(currentJob), "The current job does not belong to a batch; only Detached scheduling is valid.");

		if (delay < TimeSpan.Zero)
			ArgumentOutOfRangeException.Throw(nameof(delay), $"A job delay cannot be negative. (delay: {delay:c})");

		JobStorageCapabilityGuards.RequireGraph(Storage);

		var now = TimeProvider.GetUtcNow();
		var record = CreateRecord(payload, delay == TimeSpan.Zero ? JobState.Pending : JobState.Scheduled, runAt: now, now, groupId);
		if (options != ContinuationOptions.Detached)
			record = record with { BatchId = currentJob.BatchId };

		currentJob.Buffer.Add(
			new JobContinuationAddition
			{
				Job = record,
				Options = options,
				Delay = delay,
			}
		);

		JobTelemetry.Enqueued(JobName, QueueName);
		return record.JobId;
	}

	private JobRecord CreateRecord(TPayload payload, JobState state, DateTimeOffset runAt, DateTimeOffset now, string? groupId = null)
	{
		var (traceParent, traceState) = Activity.Current;
		var context = CaptureContext();

		return new JobRecord
		{
			JobId = JobHandle.FromString(idGenerator.CreateId(IdKind.Job)),
			JobName = JobName,
			QueueName = QueueName,
			GroupId = NormalizeGroupId(groupId),
			Payload = Serializer.Serialize(payload, payloadTypeInfoFactory),
			State = state,
			DueAt = runAt,
			CreatedAt = now,
			TraceParent = traceParent,
			TraceState = traceState,
			Context = context,
		};
	}

	private static string? NormalizeGroupId(string? groupId)
	{
		if (string.IsNullOrWhiteSpace(groupId))
			return null;
		if (groupId.Length > 128)
			throw new ArgumentException("A fair queue group id cannot exceed 128 characters.", nameof(groupId));
		return groupId;
	}

	/// <summary>
	/// 	Validates and persists a dynamic recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The unique schedule name.
	/// </param>
	/// <param name="cron">
	/// 	The cron expression that determines occurrences.
	/// </param>
	/// <param name="timeZone">
	/// 	The time zone used to evaluate <paramref name="cron"/>.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	A task that completes when the schedule has been persisted.
	/// </returns>
	protected async ValueTask AddOrUpdateRecurringCoreAsync(
		string name,
		string cron,
		string timeZone,
		CancellationToken cancellationToken
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		var zone = JobCron.GetTimeZone(timeZone);
		var expression = JobCron.Parse(cron);

		var next = expression.GetNextOccurrence(TimeProvider.GetUtcNow(), zone)
			?? throw new ArgumentException("The cron expression has no future occurrence.", nameof(cron));

		await JobStorageCapabilityGuards.RequireRecurring(Storage)
			.UpsertRecurringAsync(
				new()
				{
					Name = name,
					JobName = JobName,
					Cron = cron,
					QueueName = QueueName,
					TimeZone = timeZone,
					IsCodeDefined = false,
					NextRunAt = next,
				},
				cancellationToken
			)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// 	Removes a dynamic recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The unique schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	A task that completes when the schedule has been removed.
	/// </returns>
	protected async ValueTask RemoveRecurringCoreAsync(
		string name,
		CancellationToken cancellationToken
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		await JobStorageCapabilityGuards.RequireRecurring(Storage)
			.RemoveRecurringAsync(name, cancellationToken)
			.ConfigureAwait(false);
	}
}

using Cronos;
using System.Text.Json.Serialization.Metadata;

namespace Immediate.Jobs.Shared;

#pragma warning disable CA1068 // GroupId follows the existing token to keep positional default calls source-compatible.

/// <summary>A typed enqueue and scheduling contract implemented by every generated scheduler.</summary>
public interface IJobScheduler<TPayload>
{
	/// <summary>Enqueues work immediately and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> EnqueueAsync(TPayload payload, CancellationToken cancellationToken = default);

	/// <summary>Enqueues grouped work immediately and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> EnqueueAsync(
		TPayload payload,
		CancellationToken cancellationToken,
		string? groupId
	) =>
		string.IsNullOrWhiteSpace(groupId)
			? EnqueueAsync(payload, cancellationToken)
			: throw new NotSupportedException("This scheduler does not support fair queue group ids.");

	/// <summary>Schedules work after a delay and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> ScheduleAsync(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default);

	/// <summary>Schedules grouped work after a delay and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		TimeSpan delay,
		CancellationToken cancellationToken,
		string? groupId
	) =>
		string.IsNullOrWhiteSpace(groupId)
			? ScheduleAsync(payload, delay, cancellationToken)
			: throw new NotSupportedException("This scheduler does not support fair queue group ids.");

	/// <summary>Schedules work at an absolute time and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> ScheduleAtAsync(TPayload payload, DateTimeOffset runAt, CancellationToken cancellationToken = default);

	/// <summary>Schedules grouped work at an absolute time and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> ScheduleAtAsync(
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken,
		string? groupId
	) =>
		string.IsNullOrWhiteSpace(groupId)
			? ScheduleAtAsync(payload, runAt, cancellationToken)
			: throw new NotSupportedException("This scheduler does not support fair queue group ids.");
}

#pragma warning restore CA1068

/// <summary>Triggers a payloadless job immediately.</summary>
public interface IRecurringJobTrigger
{
	/// <summary>Enqueues the job immediately and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> TriggerNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>Dynamic recurring operations exposed by payloadless jobs without a code-defined cron.</summary>
public interface IRecurringJobScheduler : IRecurringJobTrigger
{
	/// <summary>Adds or replaces a durable dynamic schedule.</summary>
	ValueTask AddOrUpdateRecurringAsync(string name, string cron, string timeZone = "UTC", CancellationToken cancellationToken = default);

	/// <summary>Removes a dynamic durable schedule.</summary>
	ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>Runtime base used by source-generated typed schedulers.</summary>
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
	/// <summary>Captures the context envelope persisted with a new invocation.</summary>
	protected virtual string? CaptureContext() => null;

	/// <summary>The storage provider.</summary>
	protected IJobStorage Storage { get; } = storage;

	/// <summary>The serializer used with generated payload and context metadata.</summary>
	protected IJobSerializer Serializer { get; } = serializer;

	/// <summary>The clock used for deterministic scheduling.</summary>
	protected TimeProvider TimeProvider { get; } = timeProvider;

	/// <summary>The stable generated name.</summary>
	protected string JobName { get; } = jobName;

	/// <summary>The stable queue used for new invocations.</summary>
	protected string QueueName { get; } = queueName;

	/// <inheritdoc />
	public ValueTask<JobHandle> EnqueueAsync(TPayload payload, CancellationToken cancellationToken = default) =>
		ScheduleAtAsync(payload, TimeProvider.GetUtcNow(), cancellationToken);

	/// <inheritdoc />
	public ValueTask<JobHandle> EnqueueAsync(
		TPayload payload,
		CancellationToken cancellationToken,
		string? groupId
	) => ScheduleAtAsync(payload, TimeProvider.GetUtcNow(), cancellationToken, groupId);

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAsync(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default)
	{
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");

		return ScheduleAtAsync(payload, TimeProvider.GetUtcNow() + delay, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		TimeSpan delay,
		CancellationToken cancellationToken,
		string? groupId
	)
	{
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");

		return ScheduleAtAsync(payload, TimeProvider.GetUtcNow() + delay, cancellationToken, groupId);
	}

	/// <inheritdoc />
	public ValueTask<JobHandle> ScheduleAtAsync(
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken = default
	)
		=> ScheduleAtAsync(payload, runAt, cancellationToken, groupId: null);

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAtAsync(
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken,
		string? groupId
	)
	{
		var record = CreateRecord(payload, runAt, groupId);
		await Storage.EnqueueAsync(record, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return new(record.Id);
	}

	/// <summary>Adds work to an atomic batch for immediate execution after commit.</summary>
	public JobHandle AddToBatch(
		IJobBatch batch,
		TPayload payload,
		TimeSpan? delay = null
	)
	{
		ArgumentNullException.ThrowIfNull(batch);
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");
		return AddToBatchAt(batch, payload, TimeProvider.GetUtcNow() + (delay ?? TimeSpan.Zero));
	}

	/// <summary>Adds absolute-time work to an atomic batch.</summary>
	public JobHandle AddToBatchAt(
		IJobBatch batch,
		TPayload payload,
		DateTimeOffset runAt
	)
	{
		ArgumentNullException.ThrowIfNull(batch);
		_ = JobStorageCapabilityGuards.RequireGraph(Storage);
		if (batch is not JobBatch jobBatch)
			throw new ArgumentException("The batch was not created by Immediate.Jobs.", nameof(batch));
		jobBatch.EnsureOpen();
		var record = CreateRecord(payload, runAt);
		return jobBatch.Add(record, [], ContinuationTrigger.Success);
	}

	/// <summary>Schedules work after one parent job.</summary>
	public ValueTask<JobHandle> ScheduleAfterAsync(
		JobHandle parent,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.Success,
		TimeSpan? delay = null,
		CancellationToken cancellationToken = default
	) => ScheduleAfterAsync([parent], payload, on, delay, cancellationToken);

	/// <summary>Schedules work after every supplied parent job.</summary>
	public ValueTask<JobHandle> ScheduleAfterAsync(
		ReadOnlySpan<JobHandle> parents,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.Success,
		TimeSpan? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");
		if (parents.IsEmpty)
			throw new ArgumentException("At least one continuation parent is required.", nameof(parents));
		return ScheduleAfterCoreAsync(parents.ToArray(), payload, on, delay, cancellationToken);
	}

	/// <summary>Schedules work after a whole batch reaches a terminal state.</summary>
	public async ValueTask<JobHandle> ScheduleAfterAsync(
		BatchHandle parent,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.Success,
		TimeSpan? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parent);
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");
		var graphStorage = JobStorageCapabilityGuards.RequireGraph(Storage);
		var record = CreateRecord(payload, TimeProvider.GetUtcNow() + (delay ?? TimeSpan.Zero));
		var waiting = record with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		await graphStorage.EnqueueContinuationAsync(
			waiting,
			[new() { ChildJobId = record.Id, ParentBatchId = parent.Id, Trigger = on }],
			cancellationToken
		).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return new(record.Id);
	}

	/// <summary>Buffers work relative to the running job and persists it only if the attempt succeeds.</summary>
	public JobHandle ScheduleAfter(
		JobDetails current,
		TPayload payload,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	)
	{
		ArgumentNullException.ThrowIfNull(current);
		_ = JobStorageCapabilityGuards.RequireGraph(Storage);
		var buffer = current.Buffer
			?? throw new ImmediateJobException("JobDetails can schedule work only during its active execution attempt.");
		if (options != ContinuationOptions.Detached && current.BatchId is null)
			throw new ImmediateJobException("The current job does not belong to a batch; only Detached scheduling is valid.");

		var record = CreateRecord(payload, TimeProvider.GetUtcNow());
		if (options != ContinuationOptions.Detached)
			record = record with { BatchId = current.BatchId };
		buffer.Add(new() { Job = record, Options = options });
		return new(record.Id);
	}

	/// <summary>Immediately adds concurrent work to the running job's batch.</summary>
	public async ValueTask<JobHandle> AddToBatchAsync(
		JobDetails current,
		TPayload payload,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(current);
		var graphStorage = JobStorageCapabilityGuards.RequireGraph(Storage);
		if (options == ContinuationOptions.Detached)
			throw new ImmediateJobException("IJOB020: AddToBatchAsync(JobDetails, ...) cannot create detached work.");
		if (current.Buffer is null)
			throw new ImmediateJobException("JobDetails can add work only during its active execution attempt.");
		if (current.BatchId is null)
			throw new ImmediateJobException("The current job does not belong to a batch.");

		var record = CreateRecord(payload, TimeProvider.GetUtcNow());
		record = record with { BatchId = current.BatchId };
		await graphStorage.AddBatchJobAsync(current.JobId, record, options, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return new(record.Id);
	}

	private async ValueTask<JobHandle> ScheduleAfterCoreAsync(
		JobHandle[] parents,
		TPayload payload,
		ContinuationTrigger on,
		TimeSpan? delay,
		CancellationToken cancellationToken
	)
	{
		var graphStorage = JobStorageCapabilityGuards.RequireGraph(Storage);
		var runAt = TimeProvider.GetUtcNow() + (delay ?? TimeSpan.Zero);
		var batch = parents[0].Batch;
		if (batch is not null)
		{
			batch.EnsureOpen();
			var batchRecord = CreateRecord(payload, runAt);
			return batch.Add(batchRecord, parents, on);
		}

		if (parents.Any(static parent => parent.Batch is not null))
			throw new ImmediateJobException("IJOB017: Continuation handles from unrelated scopes cannot be mixed.");

		var parentIds = parents.Select(static parent => parent.Id).ToHashSet(StringComparer.Ordinal);
		if (parentIds.Count != parents.Length)
			throw new ImmediateJobException("Duplicate continuation parents are not allowed.");
		var record = CreateRecord(payload, runAt);
		var waiting = record with { State = JobState.AwaitingContinuation, RemainingDependencies = parents.Length };
		var edges = parentIds.Select(parentId => new JobContinuationEdge
		{
			ChildJobId = record.Id,
			ParentJobId = parentId,
			Trigger = on,
		}).ToArray();
		await graphStorage.EnqueueContinuationAsync(waiting, edges, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return new(record.Id);
	}

	private JobRecord CreateRecord(TPayload payload, DateTimeOffset runAt, string? groupId = null)
	{
		groupId = NormalizeGroupId(groupId);
		var now = TimeProvider.GetUtcNow();
		var (traceParent, traceState) = TraceContextCapture.Current();
		var context = CaptureContext();
		return new()
		{
			Id = idGenerator.CreateId(IdKind.Job),
			JobName = JobName,
			QueueName = QueueName,
			GroupId = groupId,
			Payload = Serializer.Serialize(payload, payloadTypeInfoFactory),
			State = runAt <= now ? JobState.Pending : JobState.Scheduled,
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

	/// <summary>Validates and persists a dynamic recurring schedule.</summary>
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

		var recurringStorage = JobStorageCapabilityGuards.RequireRecurring(Storage);
		await recurringStorage.UpsertRecurringAsync(
			new()
			{
				Name = name,
				JobName = JobName,
				Cron = cron,
				TimeZone = timeZone,
				IsCodeDefined = false,
				NextRunAt = next,
			},
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <summary>Removes a dynamic recurring schedule.</summary>
	protected ValueTask RemoveRecurringCoreAsync(
		string name,
		CancellationToken cancellationToken
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		return JobStorageCapabilityGuards.RequireRecurring(Storage)
			.RemoveRecurringAsync(name, cancellationToken);
	}
}

internal static class JobCron
{
	public static CronExpression Parse(string cron)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cron);
		var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
		if (fields is not (5 or 6))
			throw new CronFormatException("A cron expression must contain five or six fields.");

		return CronExpression.Parse(cron, fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
	}

	public static TimeZoneInfo GetTimeZone(string timeZone)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
		}
		catch (TimeZoneNotFoundException exception)
		{
			throw new ArgumentException($"Unknown time-zone identifier '{timeZone}'.", nameof(timeZone), exception);
		}
	}
}

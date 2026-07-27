using Cronos;
using System.Text.Json.Serialization.Metadata;

namespace Immediate.Jobs.Shared;

/// <summary>A typed enqueue and scheduling contract implemented by every generated scheduler.</summary>
public interface IJobScheduler<TPayload>
{
	/// <summary>Enqueues work immediately and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> Enqueue(TPayload payload, CancellationToken cancellationToken = default);

	/// <summary>Schedules work after a delay and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> Schedule(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default);

	/// <summary>Schedules work at an absolute time and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> ScheduleAt(TPayload payload, DateTimeOffset runAt, CancellationToken cancellationToken = default);
}

/// <summary>Triggers a payloadless job immediately.</summary>
public interface IRecurringJobTrigger
{
	/// <summary>Enqueues the job immediately and returns its opaque invocation identifier.</summary>
	ValueTask<JobHandle> TriggerNow(CancellationToken cancellationToken = default);
}

/// <summary>Dynamic recurring operations exposed by payloadless jobs without a code-defined cron.</summary>
public interface IRecurringJobScheduler : IRecurringJobTrigger
{
	/// <summary>Adds or replaces a durable dynamic schedule.</summary>
	ValueTask AddOrUpdateRecurring(string name, string cron, string timeZone = "UTC", CancellationToken cancellationToken = default);

	/// <summary>Removes a dynamic durable schedule.</summary>
	ValueTask RemoveRecurring(string name, CancellationToken cancellationToken = default);
}

/// <summary>Runtime base used by source-generated typed schedulers.</summary>
public abstract class JobScheduler<TPayload>(
	IJobStorage storage,
	IJobSerializer serializer,
	TimeProvider timeProvider,
	string jobName,
	string queueName,
	Func<System.Text.Json.JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
) : IJobScheduler<TPayload>
{
	/// <summary>Captures the context envelope persisted with a new invocation.</summary>
	protected virtual ValueTask<string?> CaptureContextAsync(CancellationToken cancellationToken) =>
		ValueTask.FromResult<string?>(null);

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
	public ValueTask<JobHandle> Enqueue(TPayload payload, CancellationToken cancellationToken = default) =>
		ScheduleAt(payload, TimeProvider.GetUtcNow(), cancellationToken);

	/// <inheritdoc />
	public ValueTask<JobHandle> Schedule(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default)
	{
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");

		return ScheduleAt(payload, TimeProvider.GetUtcNow() + delay, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobHandle> ScheduleAt(
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken = default
	)
	{
		var record = await CreateRecordAsync(payload, runAt, cancellationToken).ConfigureAwait(false);
		await Storage.EnqueueAsync(record, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return new(record.Id);
	}

	/// <summary>Adds work to an atomic batch for immediate execution after commit.</summary>
	public ValueTask<JobHandle> AddToBatch(
		IJobBatch batch,
		TPayload payload,
		TimeSpan? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(batch);
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");
		return AddToBatchAt(batch, payload, TimeProvider.GetUtcNow() + (delay ?? TimeSpan.Zero), cancellationToken);
	}

	/// <summary>Adds absolute-time work to an atomic batch.</summary>
	public async ValueTask<JobHandle> AddToBatchAt(
		IJobBatch batch,
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(batch);
		if (batch is not JobBatch jobBatch)
			throw new ArgumentException("The batch was not created by Immediate.Jobs.", nameof(batch));
		jobBatch.EnsureOpen();
		var record = await CreateRecordAsync(payload, runAt, cancellationToken).ConfigureAwait(false);
		return jobBatch.Add(record, [], ContinuationTrigger.AllSucceeded);
	}

	/// <summary>Schedules work after one parent job.</summary>
	public ValueTask<JobHandle> ScheduleAfter(
		JobHandle parent,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.AllSucceeded,
		TimeSpan? delay = null,
		CancellationToken cancellationToken = default
	) => ScheduleAfter([parent], payload, on, delay, cancellationToken);

	/// <summary>Schedules work after every supplied parent job.</summary>
	public ValueTask<JobHandle> ScheduleAfter(
		ReadOnlySpan<JobHandle> parents,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.AllSucceeded,
		TimeSpan? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");
		if (parents.IsEmpty)
			throw new ArgumentException("At least one continuation parent is required.", nameof(parents));
		return ScheduleAfterCore(parents.ToArray(), payload, on, delay, cancellationToken);
	}

	/// <summary>Schedules work after a whole batch reaches a terminal state.</summary>
	public async ValueTask<JobHandle> ScheduleAfter(
		BatchHandle parent,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.AllSucceeded,
		TimeSpan? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(parent);
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");
		var record = await CreateRecordAsync(
			payload,
			TimeProvider.GetUtcNow() + (delay ?? TimeSpan.Zero),
			cancellationToken
		).ConfigureAwait(false);
		var waiting = record with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		await Storage.EnqueueContinuationAsync(
			waiting,
			[new() { ChildJobId = record.Id, ParentBatchId = parent.Id, Trigger = on }],
			cancellationToken
		).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return new(record.Id);
	}

	/// <summary>Buffers work relative to the running job and persists it only if the attempt succeeds.</summary>
	public async ValueTask<JobHandle> ScheduleAfter(
		JobDetails current,
		TPayload payload,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(current);
		var buffer = current.Buffer
			?? throw new InvalidOperationException("JobDetails can schedule work only during its active execution attempt.");
		if (options != ContinuationOptions.Detached && current.BatchId is null)
			throw new InvalidOperationException("The current job does not belong to a batch; only Detached scheduling is valid.");

		var record = await CreateRecordAsync(payload, TimeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
		if (options != ContinuationOptions.Detached)
			record = record with { BatchId = current.BatchId };
		buffer.Add(new() { Job = record, Options = options });
		return new(record.Id);
	}

	/// <summary>Immediately adds concurrent work to the running job's batch.</summary>
	public async ValueTask<JobHandle> AddToBatch(
		JobDetails current,
		TPayload payload,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(current);
		if (options == ContinuationOptions.Detached)
			throw new InvalidOperationException("IJOB020: AddToBatch(JobDetails, ...) cannot create detached work.");
		if (current.Buffer is null)
			throw new InvalidOperationException("JobDetails can add work only during its active execution attempt.");
		if (current.BatchId is null)
			throw new InvalidOperationException("The current job does not belong to a batch.");

		var record = await CreateRecordAsync(payload, TimeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
		record = record with { BatchId = current.BatchId };
		await Storage.AddBatchJobAsync(current.JobId, record, options, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return new(record.Id);
	}

	private async ValueTask<JobHandle> ScheduleAfterCore(
		JobHandle[] parents,
		TPayload payload,
		ContinuationTrigger on,
		TimeSpan? delay,
		CancellationToken cancellationToken
	)
	{
		var runAt = TimeProvider.GetUtcNow() + (delay ?? TimeSpan.Zero);
		var batch = parents[0].Batch;
		if (batch is not null)
		{
			batch.EnsureOpen();
			var batchRecord = await CreateRecordAsync(payload, runAt, cancellationToken).ConfigureAwait(false);
			return batch.Add(batchRecord, parents, on);
		}

		if (parents.Any(static parent => parent.Batch is not null))
			throw new InvalidOperationException("IJOB017: Continuation handles from unrelated scopes cannot be mixed.");

		var parentIds = parents.Select(static parent => parent.Id).ToHashSet(StringComparer.Ordinal);
		if (parentIds.Count != parents.Length)
			throw new InvalidOperationException("Duplicate continuation parents are not allowed.");
		var record = await CreateRecordAsync(payload, runAt, cancellationToken).ConfigureAwait(false);
		var waiting = record with { State = JobState.AwaitingContinuation, RemainingDependencies = parents.Length };
		var edges = parentIds.Select(parentId => new JobContinuationEdge
		{
			ChildJobId = record.Id,
			ParentJobId = parentId,
			Trigger = on,
		}).ToArray();
		await Storage.EnqueueContinuationAsync(waiting, edges, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return new(record.Id);
	}

	private async ValueTask<JobRecord> CreateRecordAsync(
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken
	)
	{
		var now = TimeProvider.GetUtcNow();
		var (traceParent, traceState) = TraceContextCapture.Current();
		var context = await CaptureContextAsync(cancellationToken).ConfigureAwait(false);
		return new()
		{
			Id = Guid.NewGuid().ToString("N"),
			JobName = JobName,
			QueueName = QueueName,
			Payload = Serializer.Serialize(payload, payloadTypeInfoFactory),
			State = runAt <= now ? JobState.Pending : JobState.Scheduled,
			DueAt = runAt,
			CreatedAt = now,
			TraceParent = traceParent,
			TraceState = traceState,
			Context = context,
		};
	}

	/// <summary>Validates and persists a dynamic recurring schedule.</summary>
	protected async ValueTask AddOrUpdateRecurringCore(
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

		await Storage.UpsertRecurringAsync(
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

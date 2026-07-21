using Cronos;
using System.Text.Json.Serialization.Metadata;

namespace Immediate.Jobs.Shared;

/// <summary>A typed enqueue and scheduling contract implemented by every generated scheduler.</summary>
public interface IJobScheduler<TPayload>
	where TPayload : IJobRequest
{
	/// <summary>Enqueues work immediately.</summary>
	ValueTask<Guid> Enqueue(TPayload payload, CancellationToken cancellationToken = default);

	/// <summary>Schedules work after a delay.</summary>
	ValueTask<Guid> Schedule(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default);

	/// <summary>Schedules work at an absolute time.</summary>
	ValueTask<Guid> ScheduleAt(TPayload payload, DateTimeOffset runAt, CancellationToken cancellationToken = default);
}

/// <summary>Recurring operations shared by generated cron schedulers.</summary>
public interface IRecurringJobScheduler
{
	/// <summary>Adds or replaces a durable dynamic schedule.</summary>
	ValueTask AddOrUpdateRecurring(string name, string cron, string timeZone = "UTC", CancellationToken cancellationToken = default);

	/// <summary>Removes a dynamic durable schedule.</summary>
	ValueTask RemoveRecurring(string name, CancellationToken cancellationToken = default);

	/// <summary>Enqueues the recurring job immediately.</summary>
	ValueTask<Guid> TriggerNow(CancellationToken cancellationToken = default);
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
	where TPayload : IJobRequest
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
	public ValueTask<Guid> Enqueue(TPayload payload, CancellationToken cancellationToken = default) =>
		ScheduleAt(payload, TimeProvider.GetUtcNow(), cancellationToken);

	/// <inheritdoc />
	public ValueTask<Guid> Schedule(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default)
	{
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");

		return ScheduleAt(payload, TimeProvider.GetUtcNow() + delay, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<Guid> ScheduleAt(
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken = default
	)
	{
		var now = TimeProvider.GetUtcNow();
		var id = Guid.NewGuid();
		var (traceParent, traceState) = TraceContextCapture.Current();
		var context = await CaptureContextAsync(cancellationToken).ConfigureAwait(false);
		var record = new JobRecord
		{
			Id = id,
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

		await Storage.EnqueueAsync(record, cancellationToken).ConfigureAwait(false);
		JobTelemetry.Enqueued(JobName, QueueName);
		return id;
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

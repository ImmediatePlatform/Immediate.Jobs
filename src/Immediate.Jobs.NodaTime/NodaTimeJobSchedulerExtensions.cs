using NodaTime;

namespace Immediate.Jobs.NodaTime;

/// <summary>NodaTime overloads for generated Immediate.Jobs scheduler contracts.</summary>
public static class NodaTimeJobSchedulerExtensions
{
	/// <summary>Schedules a payload after a NodaTime duration.</summary>
	public static ValueTask<JobHandle> Schedule<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Duration delay,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.Schedule(payload, delay.ToTimeSpan(), cancellationToken);
	}

	/// <summary>Schedules a payload at a NodaTime instant.</summary>
	public static ValueTask<JobHandle> ScheduleAt<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Instant runAt,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAt(payload, runAt.ToDateTimeOffset(), cancellationToken);
	}

	/// <summary>Adds a payload to an atomic batch after a NodaTime duration.</summary>
	public static ValueTask<JobHandle> AddToBatch<TPayload>(
		this JobScheduler<TPayload> scheduler,
		IJobBatch batch,
		TPayload payload,
		Duration? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.AddToBatch(batch, payload, delay?.ToTimeSpan(), cancellationToken);
	}

	/// <summary>Adds a payload to an atomic batch at a NodaTime instant.</summary>
	public static ValueTask<JobHandle> AddToBatchAt<TPayload>(
		this JobScheduler<TPayload> scheduler,
		IJobBatch batch,
		TPayload payload,
		Instant runAt,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.AddToBatchAt(batch, payload, runAt.ToDateTimeOffset(), cancellationToken);
	}

	/// <summary>Schedules a payload after one parent job with an optional NodaTime delay.</summary>
	public static ValueTask<JobHandle> ScheduleAfter<TPayload>(
		this JobScheduler<TPayload> scheduler,
		JobHandle parent,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.AllSucceeded,
		Duration? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfter(parent, payload, on, delay?.ToTimeSpan(), cancellationToken);
	}

	/// <summary>Schedules a payload after every supplied parent job with an optional NodaTime delay.</summary>
	public static ValueTask<JobHandle> ScheduleAfter<TPayload>(
		this JobScheduler<TPayload> scheduler,
		ReadOnlySpan<JobHandle> parents,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.AllSucceeded,
		Duration? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfter(parents, payload, on, delay?.ToTimeSpan(), cancellationToken);
	}

	/// <summary>Schedules a payload after a whole batch with an optional NodaTime delay.</summary>
	public static ValueTask<JobHandle> ScheduleAfter<TPayload>(
		this JobScheduler<TPayload> scheduler,
		BatchHandle parent,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.AllSucceeded,
		Duration? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfter(parent, payload, on, delay?.ToTimeSpan(), cancellationToken);
	}

	/// <summary>Adds or replaces a recurring schedule in the supplied NodaTime zone.</summary>
	public static ValueTask AddOrUpdateRecurring(
		this IRecurringJobScheduler scheduler,
		string name,
		string cron,
		DateTimeZone timeZone,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		ArgumentNullException.ThrowIfNull(timeZone);
		return scheduler.AddOrUpdateRecurring(name, cron, timeZone.Id, cancellationToken);
	}
}

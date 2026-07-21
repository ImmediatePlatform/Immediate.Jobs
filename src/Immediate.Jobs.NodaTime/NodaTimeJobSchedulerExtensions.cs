using NodaTime;

namespace Immediate.Jobs.NodaTime;

/// <summary>NodaTime overloads for generated Immediate.Jobs scheduler contracts.</summary>
public static class NodaTimeJobSchedulerExtensions
{
	/// <summary>Schedules a payload after a NodaTime duration.</summary>
	public static ValueTask<string> Schedule<TPayload>(
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
	public static ValueTask<string> ScheduleAt<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Instant runAt,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAt(payload, runAt.ToDateTimeOffset(), cancellationToken);
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

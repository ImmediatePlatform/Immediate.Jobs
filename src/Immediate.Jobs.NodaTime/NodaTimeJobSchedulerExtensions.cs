using Immediate.Jobs.Shared.Interfaces;
using NodaTime;

namespace Immediate.Jobs.NodaTime;

/// <summary>NodaTime overloads for generated Immediate.Jobs scheduler contracts.</summary>
public static class NodaTimeJobSchedulerExtensions
{
	/// <summary>Schedules a payload after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="delay">The duration to wait before making the job due.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Duration delay,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAsync(payload, delay.ToTimeSpan(), cancellationToken);
	}

	/// <summary>Schedules grouped payload work after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="delay">The duration to wait before making the job due.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Duration delay,
		string groupId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAsync(payload, delay.ToTimeSpan(), groupId, cancellationToken);
	}

	/// <summary>Schedules a payload at a NodaTime instant.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="at">The instant at which the job becomes due.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Instant at,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAsync(payload, at.ToDateTimeOffset(), cancellationToken);
	}

	/// <summary>Schedules grouped payload work at a NodaTime instant.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="at">The instant at which the job becomes due.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Instant at,
		string groupId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAsync(payload, at.ToDateTimeOffset(), groupId, cancellationToken);
	}

	/// <summary>Adds or replaces a recurring schedule in the supplied NodaTime zone.</summary>
	/// <param name="scheduler">The recurring-job scheduler.</param>
	/// <param name="name">The unique recurring schedule name.</param>
	/// <param name="cron">The cron expression that controls the schedule.</param>
	/// <param name="timeZone">The time zone in which to evaluate the cron expression.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task that represents the asynchronous update operation.</returns>
	public static ValueTask AddOrUpdateRecurringAsync(
		this IRecurringJobScheduler scheduler,
		string name,
		string cron,
		DateTimeZone timeZone,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		ArgumentNullException.ThrowIfNull(timeZone);
		return scheduler.AddOrUpdateRecurringAsync(name, cron, timeZone.Id, cancellationToken);
	}
}

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
	/// <param name="groupId">The optional fair-queue group identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Duration delay,
		string? groupId,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAsync(payload, delay.ToTimeSpan(), groupId, cancellationToken);
	}

	/// <summary>Schedules a payload at a NodaTime instant.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="runAt">The instant at which the job becomes due.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static ValueTask<JobHandle> ScheduleAtAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Instant runAt,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAtAsync(payload, runAt.ToDateTimeOffset(), cancellationToken);
	}

	/// <summary>Schedules grouped payload work at a NodaTime instant.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="runAt">The instant at which the job becomes due.</param>
	/// <param name="groupId">The optional fair-queue group identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static ValueTask<JobHandle> ScheduleAtAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Instant runAt,
		string? groupId,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAtAsync(payload, runAt.ToDateTimeOffset(), groupId, cancellationToken);
	}

	/// <summary>Adds a payload to an atomic batch after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to add.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="batch">The batch to add the job to.</param>
	/// <param name="payload">The payload to add.</param>
	/// <param name="delay">The optional duration to wait before making the job due.</param>
	/// <returns>A handle that identifies the job added to the batch.</returns>
	public static JobHandle AddToBatch<TPayload>(
		this JobScheduler<TPayload> scheduler,
		IJobBatch batch,
		TPayload payload,
		Duration? delay = null
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.AddToBatch(batch, payload, delay?.ToTimeSpan());
	}

	/// <summary>Adds a payload to an atomic batch at a NodaTime instant.</summary>
	/// <typeparam name="TPayload">The type of payload to add.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="batch">The batch to add the job to.</param>
	/// <param name="payload">The payload to add.</param>
	/// <param name="runAt">The instant at which the job becomes due.</param>
	/// <returns>A handle that identifies the job added to the batch.</returns>
	public static JobHandle AddToBatchAt<TPayload>(
		this JobScheduler<TPayload> scheduler,
		IJobBatch batch,
		TPayload payload,
		Instant runAt
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.AddToBatchAt(batch, payload, runAt.ToDateTimeOffset());
	}

	/// <summary>Schedules a payload after one parent job with an optional NodaTime delay.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="parent">The parent job that controls the continuation.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="on">The parent outcome that triggers the continuation.</param>
	/// <param name="delay">The optional duration to wait after the continuation is triggered.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled continuation.</returns>
	public static ValueTask<JobHandle> ScheduleAfterAsync<TPayload>(
		this JobScheduler<TPayload> scheduler,
		JobHandle parent,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.Success,
		Duration? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfterAsync(parent, payload, on, delay?.ToTimeSpan(), cancellationToken);
	}

	/// <summary>Schedules a payload after every supplied parent job with an optional NodaTime delay.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="parents">The parent jobs that control the continuation.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="on">The parent outcome that triggers the continuation.</param>
	/// <param name="delay">The optional duration to wait after the continuation is triggered.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled continuation.</returns>
	public static ValueTask<JobHandle> ScheduleAfterAsync<TPayload>(
		this JobScheduler<TPayload> scheduler,
		ReadOnlySpan<JobHandle> parents,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.Success,
		Duration? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfterAsync(parents, payload, on, delay?.ToTimeSpan(), cancellationToken);
	}

	/// <summary>Schedules a payload after a whole batch with an optional NodaTime delay.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="parent">The parent batch that controls the continuation.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="on">The batch outcome that triggers the continuation.</param>
	/// <param name="delay">The optional duration to wait after the continuation is triggered.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled continuation.</returns>
	public static ValueTask<JobHandle> ScheduleAfterAsync<TPayload>(
		this JobScheduler<TPayload> scheduler,
		BatchHandle parent,
		TPayload payload,
		ContinuationTrigger on = ContinuationTrigger.Success,
		Duration? delay = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfterAsync(parent, payload, on, delay?.ToTimeSpan(), cancellationToken);
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

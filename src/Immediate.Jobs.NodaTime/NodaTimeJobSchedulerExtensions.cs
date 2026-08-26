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
	public static async ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Duration delay,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAsync(payload, delay.ToTimeSpan(), cancellationToken);
	}

	/// <summary>Schedules grouped payload work after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="delay">The duration to wait before making the job due.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static async ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Duration delay,
		string groupId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAsync(payload, delay.ToTimeSpan(), groupId, cancellationToken);
	}

	/// <summary>Schedules a payload after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="delay">The delay before the invocation becomes due.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the scheduling operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static async ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		JobDetails currentJob,
		Duration delay,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAsync(payload, currentJob, delay.ToTimeSpan(), options, cancellationToken);
	}

	/// <summary>Schedules grouped payload work after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="delay">The delay before the invocation becomes due.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the scheduling operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static async ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		JobDetails currentJob,
		Duration delay,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAsync(payload, currentJob, delay.ToTimeSpan(), groupId, options, cancellationToken);
	}

	/// <summary>Schedules a payload at a NodaTime instant.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="at">The instant at which the job becomes due.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static async ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Instant at,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAsync(payload, at.ToDateTimeOffset(), cancellationToken);
	}

	/// <summary>Schedules grouped payload work at a NodaTime instant.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="at">The instant at which the job becomes due.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static async ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		Instant at,
		string groupId,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAsync(payload, at.ToDateTimeOffset(), groupId, cancellationToken);
	}

	/// <summary>Schedules a payload after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="at">The instant at which the job becomes due.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the scheduling operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static async ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		JobDetails currentJob,
		Instant at,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAsync(payload, currentJob, at.ToDateTimeOffset(), options, cancellationToken);
	}

	/// <summary>Schedules grouped payload work after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="at">The instant at which the job becomes due.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the scheduling operation.</param>
	/// <returns>A task whose result identifies the scheduled job.</returns>
	public static async ValueTask<JobHandle> ScheduleAsync<TPayload>(
		this IJobScheduler<TPayload> scheduler,
		TPayload payload,
		JobDetails currentJob,
		Instant at,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAsync(payload, currentJob, at.ToDateTimeOffset(), groupId, options, cancellationToken);
	}

	/// <summary>Schedules a continuation after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="parent">The activity that must complete before the continuation is released.</param>
	/// <param name="delay">The delay applied when the continuation is released.</param>
	/// <param name="on">The parent outcome that releases the continuation.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled continuation.</returns>
	public static async ValueTask<JobHandle> ScheduleAfterAsync<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, ContinuationHandle parent, Duration delay, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAfterAsync(payload, parent, delay.ToTimeSpan(), on, cancellationToken);
	}

	/// <summary>Schedules a grouped continuation after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="parent">The activity that must complete before the continuation is released.</param>
	/// <param name="delay">The delay applied when the continuation is released.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="on">The parent outcome that releases the continuation.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled continuation.</returns>
	public static async ValueTask<JobHandle> ScheduleAfterAsync<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, ContinuationHandle parent, Duration delay, string groupId, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAfterAsync(payload, parent, delay.ToTimeSpan(), groupId, on, cancellationToken);
	}

	/// <summary>Schedules a fan-in continuation after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="parents">The activities that must all complete before the continuation is released.</param>
	/// <param name="delay">The delay applied when the continuation is released.</param>
	/// <param name="on">The parent outcomes that release the continuation.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled continuation.</returns>
	public static async ValueTask<JobHandle> ScheduleAfterAsync<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, IReadOnlyList<ContinuationHandle> parents, Duration delay, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAfterAsync(payload, parents, delay.ToTimeSpan(), on, cancellationToken);
	}

	/// <summary>Schedules a grouped fan-in continuation after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="parents">The activities that must all complete before the continuation is released.</param>
	/// <param name="delay">The delay applied when the continuation is released.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="on">The parent outcomes that release the continuation.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task whose result identifies the scheduled continuation.</returns>
	public static async ValueTask<JobHandle> ScheduleAfterAsync<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, IReadOnlyList<ContinuationHandle> parents, Duration delay, string groupId, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return await scheduler.ScheduleAfterAsync(payload, parents, delay.ToTimeSpan(), groupId, on, cancellationToken);
	}

	/// <summary>Schedules an in-execution continuation after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="currentJob">The currently executing job.</param>
	/// <param name="delay">The delay before the continuation becomes due.</param>
	/// <param name="options">The placement of the new work in the continuation graph.</param>
	/// <returns>A handle for the scheduled continuation.</returns>
	public static JobHandle ScheduleAfter<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, JobDetails currentJob, Duration delay, ContinuationOptions options = ContinuationOptions.BeforeContinuations)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfter(payload, currentJob, delay.ToTimeSpan(), options);
	}

	/// <summary>Schedules a grouped in-execution continuation after a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="currentJob">The currently executing job.</param>
	/// <param name="delay">The delay before the continuation becomes due.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="options">The placement of the new work in the continuation graph.</param>
	/// <returns>A handle for the scheduled continuation.</returns>
	public static JobHandle ScheduleAfter<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, JobDetails currentJob, Duration delay, string groupId, ContinuationOptions options = ContinuationOptions.BeforeContinuations)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfter(payload, currentJob, delay.ToTimeSpan(), groupId, options);
	}

	/// <summary>Adds delayed work to a batch using a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to add.</param>
	/// <param name="batch">The open batch to which the job is added.</param>
	/// <param name="delay">The delay before the job becomes due.</param>
	/// <returns>A handle for the buffered batch job.</returns>
	public static BatchJobHandle Schedule<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, Batch batch, Duration delay)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.Schedule(payload, batch, delay.ToTimeSpan());
	}

	/// <summary>Adds grouped delayed work to a batch using a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to add.</param>
	/// <param name="batch">The open batch to which the job is added.</param>
	/// <param name="delay">The delay before the job becomes due.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <returns>A handle for the buffered batch job.</returns>
	public static BatchJobHandle Schedule<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, Batch batch, Duration delay, string groupId)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.Schedule(payload, batch, delay.ToTimeSpan(), groupId);
	}

	/// <summary>Adds work to a batch at a NodaTime instant.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to add.</param>
	/// <param name="batch">The open batch to which the job is added.</param>
	/// <param name="at">The instant at which the job becomes due.</param>
	/// <returns>A handle for the buffered batch job.</returns>
	public static BatchJobHandle Schedule<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, Batch batch, Instant at)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.Schedule(payload, batch, at.ToDateTimeOffset());
	}

	/// <summary>Adds grouped work to a batch at a NodaTime instant.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The payload to add.</param>
	/// <param name="batch">The open batch to which the job is added.</param>
	/// <param name="at">The instant at which the job becomes due.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <returns>A handle for the buffered batch job.</returns>
	public static BatchJobHandle Schedule<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, Batch batch, Instant at, string groupId)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.Schedule(payload, batch, at.ToDateTimeOffset(), groupId);
	}

	/// <summary>Adds a delayed batch continuation using a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="parent">The batch job that must complete before this work is released.</param>
	/// <param name="delay">The delay applied when the continuation is released.</param>
	/// <param name="on">The parent-job outcome that releases the continuation.</param>
	/// <returns>A handle for the buffered batch continuation.</returns>
	public static BatchJobHandle ScheduleAfter<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, BatchJobHandle parent, Duration delay, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfter(payload, parent, delay.ToTimeSpan(), on);
	}

	/// <summary>Adds a grouped delayed batch continuation using a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="parent">The batch job that must complete before this work is released.</param>
	/// <param name="delay">The delay applied when the continuation is released.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="on">The parent-job outcome that releases the continuation.</param>
	/// <returns>A handle for the buffered batch continuation.</returns>
	public static BatchJobHandle ScheduleAfter<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, BatchJobHandle parent, Duration delay, string groupId, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfter(payload, parent, delay.ToTimeSpan(), groupId, on);
	}

	/// <summary>Adds a delayed batch fan-in continuation using a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="parents">The batch jobs that must all complete before this work is released.</param>
	/// <param name="delay">The delay applied when the continuation is released.</param>
	/// <param name="on">The parent-job outcomes that release the continuation.</param>
	/// <returns>A handle for the buffered batch continuation.</returns>
	public static BatchJobHandle ScheduleAfter<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, IReadOnlyList<BatchJobHandle> parents, Duration delay, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfter(payload, parents, delay.ToTimeSpan(), on);
	}

	/// <summary>Adds a grouped delayed batch fan-in continuation using a NodaTime duration.</summary>
	/// <typeparam name="TPayload">The type of payload to schedule.</typeparam>
	/// <param name="scheduler">The typed job scheduler.</param>
	/// <param name="payload">The continuation payload.</param>
	/// <param name="parents">The batch jobs that must all complete before this work is released.</param>
	/// <param name="delay">The delay applied when the continuation is released.</param>
	/// <param name="groupId">The fair-queue group identifier.</param>
	/// <param name="on">The parent-job outcomes that release the continuation.</param>
	/// <returns>A handle for the buffered batch continuation.</returns>
	public static BatchJobHandle ScheduleAfter<TPayload>(this IJobScheduler<TPayload> scheduler, TPayload payload, IReadOnlyList<BatchJobHandle> parents, Duration delay, string groupId, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		return scheduler.ScheduleAfter(payload, parents, delay.ToTimeSpan(), groupId, on);
	}

	/// <summary>Adds or replaces a recurring schedule in the supplied NodaTime zone.</summary>
	/// <param name="scheduler">The recurring-job scheduler.</param>
	/// <param name="name">The unique recurring schedule name.</param>
	/// <param name="cron">The cron expression that controls the schedule.</param>
	/// <param name="timeZone">The time zone in which to evaluate the cron expression.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	/// <returns>A task that represents the asynchronous update operation.</returns>
	public static async ValueTask AddOrUpdateRecurringAsync(
		this IRecurringJobScheduler scheduler,
		string name,
		string cron,
		DateTimeZone timeZone,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(scheduler);
		ArgumentNullException.ThrowIfNull(timeZone);
		await scheduler.AddOrUpdateRecurringAsync(name, cron, timeZone.Id, cancellationToken);
	}
}

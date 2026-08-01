namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	A typed enqueue and scheduling contract implemented by every generated scheduler.
/// </summary>
/// <typeparam name="TPayload">
/// 	The job payload type.
/// </typeparam>
public interface IJobScheduler<TPayload>
{
	/// <summary>
	///		Cancels a non-terminal durable invocation.
	/// </summary>
	/// <param name="handle">
	///		The invocation to cancel.
	/// </param>
	/// <param name="cancellationToken">
	///		A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	///		A value task that represents the asynchronous cancellation.
	/// </returns>
	ValueTask CancelAsync(JobHandle handle, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Enqueues work immediately and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload to enqueue.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the enqueue operation.
	/// </param>
	/// <returns>
	/// 	A handle for the enqueued invocation.
	/// </returns>
	ValueTask<JobHandle> EnqueueAsync(TPayload payload, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Enqueues grouped work immediately and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload to enqueue.
	/// </param>
	/// <param name="groupId">
	/// 	The optional fair queue group identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the enqueue operation.
	/// </param>
	/// <returns>
	/// 	A handle for the enqueued invocation.
	/// </returns>
	ValueTask<JobHandle> EnqueueAsync(
		TPayload payload,
		string? groupId,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules work after a delay and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload to schedule.
	/// </param>
	/// <param name="delay">
	/// 	The delay before the invocation becomes due.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled invocation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAsync(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Schedules grouped work after a delay and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload to schedule.
	/// </param>
	/// <param name="delay">
	/// 	The delay before the invocation becomes due.
	/// </param>
	/// <param name="groupId">
	/// 	The optional fair queue group identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled invocation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		TimeSpan delay,
		string? groupId,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules work at an absolute time and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload to schedule.
	/// </param>
	/// <param name="runAt">
	/// 	The absolute time at which the invocation becomes due.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled invocation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAtAsync(TPayload payload, DateTimeOffset runAt, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Schedules grouped work at an absolute time and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload to schedule.
	/// </param>
	/// <param name="runAt">
	/// 	The absolute time at which the invocation becomes due.
	/// </param>
	/// <param name="groupId">
	/// 	The optional fair queue group identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled invocation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAtAsync(
		TPayload payload,
		DateTimeOffset runAt,
		string? groupId,
		CancellationToken cancellationToken = default
	);
}

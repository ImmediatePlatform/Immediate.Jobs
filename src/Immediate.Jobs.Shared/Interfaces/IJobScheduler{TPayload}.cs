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
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the enqueue operation.
	/// </param>
	/// <returns>
	/// 	A handle for the enqueued invocation.
	/// </returns>
	ValueTask<JobHandle> EnqueueAsync(TPayload payload, string groupId, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Immediately adds work to the batch containing the currently running job, without waiting for that job to complete.
	/// </summary>
	/// <param name="payload">The payload to enqueue.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the enqueue operation.</param>
	/// <returns>A handle for the enqueued invocation.</returns>
	ValueTask<JobHandle> EnqueueAsync(
		TPayload payload,
		JobDetails currentJob,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Immediately adds grouped work to the batch containing the currently running job, without waiting for that job to complete.
	/// </summary>
	/// <param name="payload">The payload to enqueue.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="groupId">The fair queue group identifier.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the enqueue operation.</param>
	/// <returns>A handle for the enqueued invocation.</returns>
	ValueTask<JobHandle> EnqueueAsync(
		TPayload payload,
		JobDetails currentJob,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
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
	/// 	The fair queue group identifier.
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
		string groupId,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules work in the batch containing the currently running job after a delay, without waiting for that job to complete.
	/// </summary>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="delay">The delay before the invocation becomes due.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the scheduling operation.</param>
	/// <returns>A handle for the scheduled invocation.</returns>
	ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules grouped work in the batch containing the currently running job after a delay, without waiting for that job to complete.
	/// </summary>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="delay">The delay before the invocation becomes due.</param>
	/// <param name="groupId">The fair queue group identifier.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the scheduling operation.</param>
	/// <returns>A handle for the scheduled invocation.</returns>
	ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules work at an absolute time and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload to schedule.
	/// </param>
	/// <param name="at">
	/// 	The absolute time at which the invocation becomes due.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled invocation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAsync(TPayload payload, DateTimeOffset at, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Schedules grouped work at an absolute time and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload to schedule.
	/// </param>
	/// <param name="at">
	/// 	The absolute time at which the invocation becomes due.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled invocation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		DateTimeOffset at,
		string groupId,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules work in the batch containing the currently running job at an absolute time, without waiting for that job to complete.
	/// </summary>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="at">The absolute time at which the invocation becomes due.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the scheduling operation.</param>
	/// <returns>A handle for the scheduled invocation.</returns>
	ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		JobDetails currentJob,
		DateTimeOffset at,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules grouped work in the batch containing the currently running job at an absolute time, without waiting for that job to complete.
	/// </summary>
	/// <param name="payload">The payload to schedule.</param>
	/// <param name="currentJob">Details of the currently running job whose batch receives the new work.</param>
	/// <param name="at">The absolute time at which the invocation becomes due.</param>
	/// <param name="groupId">The fair queue group identifier.</param>
	/// <param name="options">The new job's relationship to continuations of the current job.</param>
	/// <param name="cancellationToken">A token that can cancel the scheduling operation.</param>
	/// <returns>A handle for the scheduled invocation.</returns>
	ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		JobDetails currentJob,
		DateTimeOffset at,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules work to run after a single job completes and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the continuation.
	/// </param>
	/// <param name="parent">
	/// 	The parent job/batch that must complete before this work is released.
	/// </param>
	/// <param name="on">
	/// 	The parent activity outcome that releases the continuation.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled continuation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		ContinuationHandle parent,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules grouped work to run after a single job completes and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the continuation.
	/// </param>
	/// <param name="parent">
	/// 	The parent job/batch that must complete before this work is released.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="on">
	/// 	The parent activity outcome that releases the continuation.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled continuation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		ContinuationHandle parent,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules work to run after a single job completes with an optional delay and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the continuation.
	/// </param>
	/// <param name="parent">
	/// 	The parent job/batch that must complete before this work is released.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="on">
	/// 	The parent activity outcome that releases the continuation.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled continuation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		ContinuationHandle parent,
		TimeSpan delay,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules grouped work to run after a single job completes with an optional delay and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the continuation.
	/// </param>
	/// <param name="parent">
	/// 	The parent job/batch that must complete before this work is released.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="on">
	/// 	The parent activity outcome that releases the continuation.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled continuation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		ContinuationHandle parent,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules work to run after all supplied jobs complete and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the continuation.
	/// </param>
	/// <param name="parents">
	/// 	The parent activities that must all complete before this work is released.
	/// </param>
	/// <param name="on">
	/// 	The parent activity outcome that releases the continuation.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled continuation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<ContinuationHandle> parents,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules grouped work to run after all supplied jobs complete and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the continuation.
	/// </param>
	/// <param name="parents">
	/// 	The parent activities that must all complete before this work is released.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="on">
	/// 	The parent activity outcome that releases the continuation.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled continuation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<ContinuationHandle> parents,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules work to run after all supplied jobs complete with a delay and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the continuation.
	/// </param>
	/// <param name="parents">
	/// 	The parent activities that must all complete before this work is released.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="on">
	/// 	The parent activity outcome that releases the continuation.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled continuation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<ContinuationHandle> parents,
		TimeSpan delay,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Schedules grouped work to run after all supplied jobs complete with a delay and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the continuation.
	/// </param>
	/// <param name="parents">
	/// 	The parent activities that must all complete before this work is released.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="on">
	/// 	The parent activity outcome that releases the continuation.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the scheduling operation.
	/// </param>
	/// <returns>
	/// 	A handle for the scheduled continuation.
	/// </returns>
	ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<ContinuationHandle> parents,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Buffers work relative to the running job and persists it only if the attempt succeeds.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the new invocation.
	/// </param>
	/// <param name="currentJob">
	/// 	Details of the currently running invocation.
	/// </param>
	/// <param name="options">
	/// 	The new invocation's relationship to existing continuations.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered invocation.
	/// </returns>
	JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	);

	/// <summary>
	/// 	Buffers grouped work relative to the running job and persists it only if the attempt succeeds.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the new invocation.
	/// </param>
	/// <param name="currentJob">
	/// 	Details of the currently running invocation.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="options">
	/// 	The new invocation's relationship to existing continuations.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered invocation.
	/// </returns>
	JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	);

	/// <summary>
	/// 	Buffers work with a delay relative to the running job and persists it only if the attempt succeeds.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the new invocation.
	/// </param>
	/// <param name="currentJob">
	/// 	Details of the currently running invocation.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="options">
	/// 	The new invocation's relationship to existing continuations.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered invocation.
	/// </returns>
	JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	);

	/// <summary>
	/// 	Buffers grouped work with a delay relative to the running job and persists it only if the attempt succeeds.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the new invocation.
	/// </param>
	/// <param name="currentJob">
	/// 	Details of the currently running invocation.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="options">
	/// 	The new invocation's relationship to existing continuations.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered invocation.
	/// </returns>
	JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	);

	/// <summary>
	/// 	Adds work to an atomic batch for immediate execution after commit and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="batch">
	/// 	The open batch to which the invocation is added.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle Enqueue(TPayload payload, Batch batch);

	/// <summary>
	/// 	Adds grouped work to an atomic batch for immediate execution after commit and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="batch">
	/// 	The open batch to which the invocation is added.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle Enqueue(TPayload payload, Batch batch, string groupId);

	/// <summary>
	/// 	Adds work with a delay to an atomic batch for execution after commit and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="batch">
	/// 	The open batch to which the invocation is added.
	/// </param>
	/// <param name="delay">
	/// 	The delay before the invocation becomes due.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay);

	/// <summary>
	/// 	Adds grouped work with a delay to an atomic batch for execution after commit and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="batch">
	/// 	The open batch to which the invocation is added.
	/// </param>
	/// <param name="delay">
	/// 	The delay before the invocation becomes due.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay, string groupId);

	/// <summary>
	/// 	Adds work at an absolute time to an atomic batch for execution after commit and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="batch">
	/// 	The open batch to which the invocation is added.
	/// </param>
	/// <param name="at">
	/// 	The absolute time at which the invocation becomes due.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at);

	/// <summary>
	/// 	Adds grouped work at an absolute time to an atomic batch for execution after commit and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="batch">
	/// 	The open batch to which the invocation is added.
	/// </param>
	/// <param name="at">
	/// 	The absolute time at which the invocation becomes due.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at, string groupId);

	/// <summary>
	/// 	Adds work to a batch that runs after a single batch job completes and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="job">
	/// 	The batch job that must complete before this work is released.
	/// </param>
	/// <param name="on">
	/// 	The parent-job outcome that releases the continuation.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		ContinuationTrigger on = ContinuationTrigger.Success
	);

	/// <summary>
	/// 	Adds grouped work to a batch that runs after a single batch job completes and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="job">
	/// 	The batch job that must complete before this work is released.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="on">
	/// 	The parent-job outcome that releases the continuation.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	);

	/// <summary>
	/// 	Adds work with a delay to a batch that runs after a single batch job completes and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="job">
	/// 	The batch job that must complete before this work is released.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="on">
	/// 	The parent-job outcome that releases the continuation.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		TimeSpan delay,
		ContinuationTrigger on = ContinuationTrigger.Success
	);

	/// <summary>
	/// 	Adds grouped work with a delay to a batch that runs after a single batch job completes and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="job">
	/// 	The batch job that must complete before this work is released.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="on">
	/// 	The parent-job outcome that releases the continuation.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	);

	/// <summary>
	/// 	Adds work to a batch that runs after all supplied batch jobs complete and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="jobs">
	/// 	The batch jobs that must all complete before this work is released.
	/// </param>
	/// <param name="on">
	/// 	The parent-jobs outcome that releases the continuation.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		ContinuationTrigger on = ContinuationTrigger.Success
	);

	/// <summary>
	/// 	Adds grouped work to a batch that runs after all supplied batch jobs complete and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="jobs">
	/// 	The batch jobs that must all complete before this work is released.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="on">
	/// 	The parent-jobs outcome that releases the continuation.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	);

	/// <summary>
	/// 	Adds work with a delay to a batch that runs after all supplied batch jobs complete and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="jobs">
	/// 	The batch jobs that must all complete before this work is released.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="on">
	/// 	The parent-jobs outcome that releases the continuation.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		TimeSpan delay,
		ContinuationTrigger on = ContinuationTrigger.Success
	);

	/// <summary>
	/// 	Adds grouped work with a delay to a batch that runs after all supplied batch jobs complete and returns its handle.
	/// </summary>
	/// <param name="payload">
	/// 	The payload for the invocation.
	/// </param>
	/// <param name="jobs">
	/// 	The batch jobs that must all complete before this work is released.
	/// </param>
	/// <param name="delay">
	/// 	The delay applied when the continuation is released.
	/// </param>
	/// <param name="groupId">
	/// 	The fair queue group identifier.
	/// </param>
	/// <param name="on">
	/// 	The parent-jobs outcome that releases the continuation.
	/// </param>
	/// <returns>
	/// 	A handle for the buffered batch invocation.
	/// </returns>
	BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	);

	/// <summary>
	///		Cancels a non-terminal durable invocation.
	/// </summary>
	/// <param name="job">
	///		The invocation to cancel.
	/// </param>
	/// <param name="cancellationToken">
	///		A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	///		A value task that represents the asynchronous cancellation.
	/// </returns>
	ValueTask CancelAsync(JobHandle job, CancellationToken cancellationToken = default);
}

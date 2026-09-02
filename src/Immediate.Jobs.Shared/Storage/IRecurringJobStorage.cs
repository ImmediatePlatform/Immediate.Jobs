using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Storage capability for recurring schedules and occurrence materialization.
/// </summary>
public interface IRecurringJobStorage : IJobStorage
{
	/// <summary>
	///     Resets the list of code-defined recurring job schedules to the provided list.
	/// </summary>
	/// <param name="schedules">
	///     The complete list of code-defined recurring job schedules.
	/// </param>
	/// <param name="cancellationToken">
	///     A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	///     A value task that represents the asynchronous merge.
	/// </returns>
	/// <remarks>
	///	    This method should be called exactly once per app start, after <see
	///	    cref="IJobStorage.InitializeAsync(CancellationToken)"/> to reset the list of code-defined cron jobs to the
	///	    currently compiled list.
	/// </remarks>
	ValueTask MergeRecurringSchedulesListAsync(
		IReadOnlyList<RecurringJobSchedule> schedules,
		CancellationToken cancellationToken = default
	);
	/// <summary>
	/// 	Creates or updates a recurring schedule.
	/// </summary>
	/// <param name="schedule">
	/// 	The recurring schedule to create or update.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous upsert.
	/// </returns>
	ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Removes a dynamic recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The recurring schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous removal.
	/// </returns>
	ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Pauses a recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The recurring schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous pause operation.
	/// </returns>
	ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Resumes a recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The recurring schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	A value task that represents the asynchronous resume operation.
	/// </returns>
	ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Returns schedules ready to materialize.
	/// </summary>
	/// <param name="now">
	/// 	The current UTC time used to determine which schedules are due.
	/// </param>
	/// <param name="batchSize">
	/// 	The maximum number of schedules to return.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The recurring schedules that are ready to materialize.
	/// </returns>
	ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// 	Atomically creates a recurring invocation and advances the schedule.
	/// </summary>
	/// <param name="schedule">
	/// 	The recurring schedule being materialized.
	/// </param>
	/// <param name="job">
	/// 	The invocation to insert.
	/// </param>
	/// <param name="nextRunAt">
	/// 	The next UTC occurrence for the schedule.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns><see langword="true"/> when the occurrence was materialized; otherwise, <see langword="false"/>.
	/// </returns>
	ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken = default
	);
}

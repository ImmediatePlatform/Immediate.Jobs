namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	Dynamic recurring operations exposed by payloadless jobs without a code-defined cron.
/// </summary>
public interface IRecurringJobScheduler : IRecurringJobTrigger
{
	/// <summary>
	/// 	Adds or replaces a durable dynamic schedule.
	/// </summary>
	/// <param name="name">
	/// 	The unique schedule name.
	/// </param>
	/// <param name="cron">
	/// 	The cron expression that determines occurrences.
	/// </param>
	/// <param name="timeZone">
	/// 	The time zone used to evaluate <paramref name="cron"/>.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	A task that completes when the schedule has been persisted.
	/// </returns>
	ValueTask AddOrUpdateRecurringAsync(string name, string cron, string timeZone = "UTC", CancellationToken cancellationToken = default);

	/// <summary>
	/// 	Removes a dynamic durable schedule.
	/// </summary>
	/// <param name="name">
	/// 	The unique schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	A task that completes when the schedule has been removed.
	/// </returns>
	ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default);
}

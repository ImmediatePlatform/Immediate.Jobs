namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	Triggers a payloadless job immediately.
/// </summary>
public interface IRecurringJobTrigger
{
	/// <summary>
	/// 	Enqueues the job immediately and returns its opaque invocation identifier.
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the trigger operation.
	/// </param>
	/// <returns>
	/// 	A handle for the triggered invocation.
	/// </returns>
	ValueTask<JobHandle> TriggerNowAsync(CancellationToken cancellationToken = default);
}

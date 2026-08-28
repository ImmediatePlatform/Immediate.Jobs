using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	A continuation buffered during a running job attempt and committed only on success.
/// </summary>
public sealed record JobContinuationAddition
{
	/// <summary>
	/// 	The fully serialized new invocation.
	/// </summary>
	public required JobRecord Job { get; init; }

	/// <summary>
	/// 	How the new invocation joins and modifies the current workflow.
	/// </summary>
	public required ContinuationOptions Options { get; init; }

	/// <summary>
	///		The amount of delay after the current job completes to schedule the new job.
	/// </summary>
	public required TimeSpan Delay { get; init; }

	/// <summary>
	/// 	The dependency trigger from the running job to the new invocation.
	/// </summary>
	public ContinuationTrigger Trigger { get; init; } = ContinuationTrigger.Success;
}

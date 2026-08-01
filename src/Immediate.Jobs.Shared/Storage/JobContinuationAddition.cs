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
	/// <value>
	/// 	The new invocation to commit.
	/// </value>
	public required JobRecord Job { get; init; }
	/// <summary>
	/// 	How the new invocation joins and modifies the current workflow.
	/// </summary>
	/// <value>
	/// 	The workflow-joining behavior.
	/// </value>
	public required ContinuationOptions Options { get; init; }
	/// <summary>
	/// 	The dependency trigger from the running job to the new invocation.
	/// </summary>
	/// <value>
	/// 	The dependency trigger from the running invocation.
	/// </value>
	public ContinuationTrigger Trigger { get; init; } = ContinuationTrigger.Success;
}

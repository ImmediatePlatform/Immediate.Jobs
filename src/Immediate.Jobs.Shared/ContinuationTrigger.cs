namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Determines how a continuation evaluates its parents.
/// </summary>
public enum ContinuationTrigger
{
	/// <summary>
	/// 	Run only when every parent succeeds; otherwise skip the continuation.
	/// </summary>
	Success,

	/// <summary>
	/// 	Run only when every parent is terminal and at least one parent failed; otherwise skip the continuation.
	/// </summary>
	Failure,

	/// <summary>
	/// 	Run after every parent reaches any terminal state.
	/// </summary>
	Complete,
}

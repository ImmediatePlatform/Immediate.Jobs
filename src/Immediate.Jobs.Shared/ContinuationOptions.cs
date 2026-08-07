namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Determines how work scheduled by a running job joins its workflow.
/// </summary>
public enum ContinuationOptions
{
	/// <summary>
	/// 	Schedule outside the current batch and do not modify existing continuations.
	/// </summary>
	Detached,

	/// <summary>
	/// 	Add the job to the current batch as a parallel branch.
	/// </summary>
	BesideContinuations,

	/// <summary>
	/// 	Add the job to the current batch and make current waiters depend on it too.
	/// </summary>
	BeforeContinuations,
}

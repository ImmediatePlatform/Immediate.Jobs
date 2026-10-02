namespace Immediate.Jobs.Shared;

/// <summary>
/// 	The durable lifecycle state of a job.
/// </summary>
public enum JobState
{
	/// <summary>
	/// 	The job is parked until all continuation dependencies are satisfied.
	/// </summary>
	AwaitingContinuation,
	/// <summary>
	/// 	The job is durably created but parked until it is explicitly triggered.
	/// </summary>
	WaitingForTrigger,
	/// <summary>
	/// 	The job is delayed until its due time.
	/// </summary>
	Scheduled,
	/// <summary>
	/// 	The job is ready for acquisition.
	/// </summary>
	Pending,
	/// <summary>
	/// 	A worker owns the job lease.
	/// </summary>
	Active,
	/// <summary>
	/// 	The job completed successfully.
	/// </summary>
	Succeeded,
	/// <summary>
	/// 	The job exhausted all attempts.
	/// </summary>
	Failed,
	/// <summary>
	/// 	The job was cancelled.
	/// </summary>
	Cancelled,
	/// <summary>
	/// 	The job was not run because a continuation condition or scheduling policy was not met.
	/// </summary>
	Skipped,
}

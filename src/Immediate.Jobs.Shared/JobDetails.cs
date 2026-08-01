using Immediate.Jobs.Shared.Internals;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Immutable metadata describing one background-job execution attempt.
/// </summary>
public sealed record JobDetails
{
	/// <summary>
	///		Creates a new <see cref="JobDetails"/> from an execution record
	/// </summary>
	/// <param name="execution">
	///		The <see cref="JobExecution"/> record containing details about the currently invoked job.
	/// </param>
	public JobDetails(JobExecution execution)
	{
		ArgumentNullException.ThrowIfNull(execution);

		JobId = execution.Record.Id;
		JobName = execution.Record.JobName;
		QueueName = execution.Record.QueueName;
		Attempt = execution.Record.Attempt;
		CreatedAt = execution.Record.CreatedAt;
		ScheduledAt = execution.Record.DueAt;
		BatchId = execution.Record.BatchId;
		Buffer = execution.Buffer;
	}

	/// <summary>
	/// 	The unique invocation identifier.
	/// </summary>
	public string JobId { get; }

	/// <summary>
	/// 	The stable job name.
	/// </summary>
	public string JobName { get; }

	/// <summary>
	/// 	The persisted queue name.
	/// </summary>
	public string QueueName { get; }

	/// <summary>
	/// 	The current execution-attempt number.
	/// </summary>
	public int Attempt { get; }

	/// <summary>
	/// 	The timestamp at which the invocation was created.
	/// </summary>
	public DateTimeOffset CreatedAt { get; }

	/// <summary>
	/// 	The timestamp at which the invocation was scheduled to run.
	/// </summary>
	public DateTimeOffset ScheduledAt { get; }

	/// <summary>
	/// 	The containing batch identifier, if any.
	/// </summary>
	public string? BatchId { get; }

	/// <summary>
	/// 	Runtime continuation buffer for the current attempt.
	/// </summary>
	internal JobExecutionBuffer? Buffer { get; }
}

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
	/// 	The job is parked until required inputs are supplied.
	/// </summary>
	/// <remarks>
	/// 	This state is unused at the moment, but reserved for future work.
	/// </remarks>
	AwaitingParameters,
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

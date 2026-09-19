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

		JobHandle = execution.Record.JobHandle;
		JobName = execution.Record.JobName;
		QueueName = execution.Record.QueueName;
		Attempt = execution.Record.Attempt;
		CreatedAt = execution.Record.CreatedAt;
		ScheduledAt = execution.Record.DueAt;
		BatchHandle = execution.Record.BatchHandle;
		Buffer = execution.Buffer;
	}

	/// <summary>
	/// 	The unique invocation identifier.
	/// </summary>
	public JobHandle JobHandle { get; }

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
	public BatchHandle? BatchHandle { get; }

	/// <summary>
	/// 	Runtime continuation buffer for the current attempt.
	/// </summary>
	internal JobExecutionBuffer? Buffer { get; }
}

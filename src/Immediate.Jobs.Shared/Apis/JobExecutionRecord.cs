namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	The durable outcome of one acquired job execution.
/// </summary>
public enum JobExecutionState
{
	/// <summary>
	/// 	The execution is currently owned by a worker.
	/// </summary>
	Active,
	/// <summary>
	/// 	The execution completed successfully.
	/// </summary>
	Succeeded,
	/// <summary>
	/// 	The execution ended with an error.
	/// </summary>
	Failed,
	/// <summary>
	/// 	The execution was explicitly cancelled.
	/// </summary>
	Cancelled,
	/// <summary>
	/// 	The worker lease expired before a terminal outcome was recorded.
	/// </summary>
	Interrupted,
}

/// <summary>
/// 	Storage-neutral diagnostics retained for one acquired job execution.
/// </summary>
public sealed record JobExecutionRecord
{
	/// <summary>
	/// 	Reconstructs the latest execution available in a legacy job row.
	/// </summary>
	/// <param name="job">
	/// 	The pre-execution-history job record.
	/// </param>
	/// <returns>
	/// 	A clearly marked synthetic execution, or <see langword="null"/> when the job was never acquired.
	/// </returns>
	public static JobExecutionRecord? CreateSynthetic(JobRecord job)
	{
		ArgumentNullException.ThrowIfNull(job);
		return JobExecutionRecords.CreateSynthetic(job);
	}

	/// <summary>
	/// 	The owning job identifier.
	/// </summary>
	public required JobHandle JobId { get; init; }

	/// <summary>
	/// 	The 1-based execution ordinal and ownership-fencing value.
	/// </summary>
	public required int Attempt { get; init; }

	/// <summary>
	/// 	The current or terminal execution state.
	/// </summary>
	public required JobExecutionState State { get; init; }

	/// <summary>
	/// 	The worker that acquired the execution, when known.
	/// </summary>
	public string? WorkerId { get; init; }

	/// <summary>
	/// 	The storage time at which the execution was acquired, when known.
	/// </summary>
	public DateTimeOffset? AcquiredAt { get; init; }

	/// <summary>
	/// 	The worker time immediately before handler execution began, when recorded.
	/// </summary>
	public DateTimeOffset? ExecutionStartedAt { get; init; }

	/// <summary>
	/// 	The storage time at which the execution reached its terminal state.
	/// </summary>
	public DateTimeOffset? CompletedAt { get; init; }

	/// <summary>
	/// 	The execution trace identifier, when an activity was created.
	/// </summary>
	public string? ExecutionTraceId { get; init; }

	/// <summary>
	/// 	The execution span identifier, when an activity was created.
	/// </summary>
	public string? ExecutionSpanId { get; init; }

	/// <summary>
	/// 	The complete exception text for a failed execution.
	/// </summary>
	public string? Error { get; init; }

	/// <summary>
	/// 	Whether this best-effort record was reconstructed from a pre-history job row.
	/// </summary>
	public bool IsSynthetic { get; init; }
}

internal static class JobExecutionRecords
{
	internal static JobExecutionRecord? CreateSynthetic(JobRecord job)
	{
		if (job.Attempt <= 0)
			return null;

		var state = job.State switch
		{
			JobState.Active => JobExecutionState.Active,
			JobState.Succeeded => JobExecutionState.Succeeded,
			JobState.Failed => JobExecutionState.Failed,
			JobState.Cancelled => JobExecutionState.Cancelled,
			JobState.Pending or JobState.Scheduled when job.LastError is not null => JobExecutionState.Failed,
			JobState.AwaitingContinuation or
			JobState.AwaitingParameters or
			JobState.Scheduled or
			JobState.Pending or
			JobState.Skipped => JobExecutionState.Interrupted,
			_ => throw new ArgumentOutOfRangeException(nameof(job), job.State, "Unknown job state."),
		};

		return new JobExecutionRecord()
		{
			JobId = job.JobId,
			Attempt = job.Attempt,
			State = state,
			WorkerId = job.State == JobState.Active ? job.WorkerId : null,
			ExecutionStartedAt = job.ExecutionStartedAt,
			CompletedAt = IsTerminal(job.State) ? job.CompletedAt : null,
			ExecutionTraceId = job.ExecutionTraceId,
			ExecutionSpanId = job.ExecutionSpanId,
			Error = state == JobExecutionState.Failed ? job.LastError : null,
			IsSynthetic = true,
		};
	}

	private static bool IsTerminal(JobState state) =>
		state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;
}

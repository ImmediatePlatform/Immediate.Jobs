namespace Immediate.Jobs.Shared;

/// <summary>
/// Optional request capability for receiving metadata about the current background-job invocation.
/// Job request types do not need to implement this interface unless they need access to <see cref="JobDetails"/>.
/// </summary>
public interface IJobRequest
{
	/// <summary>The current invocation details, or <see langword="null"/> before execution begins.</summary>
	JobDetails? JobDetails { get; set; }
}

/// <summary>Immutable metadata describing one background-job execution attempt.</summary>
public sealed record JobDetails(
	string JobId,
	string JobName,
	string QueueName,
	int Attempt,
	DateTimeOffset CreatedAt,
	DateTimeOffset ScheduledAt,
	string? BatchId = null
)
{
	/// <summary>Runtime continuation buffer for the current attempt.</summary>
	[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public JobExecutionBuffer? Buffer { get; init; }
}

/// <summary>Marker request for jobs whose handler does not otherwise require input.</summary>
public record struct EmptyJobRequest : IJobRequest
{
	/// <inheritdoc />
	public JobDetails? JobDetails { get; set; }
}

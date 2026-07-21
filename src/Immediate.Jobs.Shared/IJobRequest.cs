namespace Immediate.Jobs.Shared;

/// <summary>A request that can receive metadata for its current background-job invocation.</summary>
public interface IJobRequest
{
	/// <summary>The current invocation details, or <see langword="null"/> before execution begins.</summary>
	JobDetails? JobDetails { get; set; }
}

/// <summary>Immutable metadata describing one background-job execution attempt.</summary>
public sealed record JobDetails(
	Guid JobId,
	string JobName,
	string QueueName,
	int Attempt,
	DateTimeOffset CreatedAt,
	DateTimeOffset ScheduledAt
);

/// <summary>Marker request for jobs whose handler does not otherwise require input.</summary>
public record struct NoPayload : IJobRequest
{
	/// <inheritdoc />
	public JobDetails? JobDetails { get; set; }
}

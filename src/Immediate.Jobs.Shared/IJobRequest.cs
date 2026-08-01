namespace Immediate.Jobs.Shared;

/// <summary>
///	    Optional request capability for receiving metadata about the current background-job invocation. Job request
///	    types do not need to implement this interface unless they need access to <see cref="JobDetails"/>.
/// </summary>
public interface IJobRequest
{
	/// <summary>
	/// 	The current invocation details, or <see langword="null"/> before execution begins.
	/// </summary>
	/// <value>
	/// 	The metadata for the current invocation attempt, when available.
	/// </value>
	JobDetails? JobDetails { get; set; }
}

/// <summary>
/// 	Marker request for jobs whose handler does not otherwise require input.
/// </summary>
public record struct EmptyJobRequest : IJobRequest
{
	/// <inheritdoc />
	public JobDetails? JobDetails { get; set; }
}

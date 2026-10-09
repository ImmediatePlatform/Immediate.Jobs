namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	A priority-ordered, node-local storage acquisition request.
/// </summary>
public sealed record JobAcquisitionRequest
{
	/// <summary>
	/// 	The identifier of the worker node taking ownership.
	/// </summary>
	public required string WorkerId { get; init; }

	/// <summary>
	/// 	The lease assigned to acquired records.
	/// </summary>
	public required TimeSpan Lease { get; init; }

	/// <summary>
	/// 	The maximum number of records to acquire.
	/// </summary>
	public required int BatchSize { get; init; }

	/// <summary>
	/// 	Queues in dispatch order, with their remaining capacities.
	/// </summary>
	public required IReadOnlyList<JobQueueAcquisition> Queues { get; init; }

	/// <summary>
	/// 	Fair queue policy for this acquisition, or <see langword="null"/> when fairness is disabled.
	/// </summary>
	public FairQueuePolicy? FairQueues { get; init; }

	/// <summary>
	/// 	Parsed, definition-wide limits keyed by job name. Missing names have no rate limits.
	/// </summary>
	public IReadOnlyDictionary<string, JobAcquisitionLimits> JobLimits { get; init; } = new Dictionary<string, JobAcquisitionLimits>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 	Returns the acquisition limits for a stable job definition name.
	/// </summary>
	/// <param name="jobName">
	/// 	The stable job definition name.
	/// </param>
	/// <returns>
	/// 	The configured limits, or an unbounded configuration when the name is absent.
	/// </returns>
	public JobAcquisitionLimits LimitsFor(string jobName) => JobLimits.GetValueOrDefault(jobName) ?? new();
}

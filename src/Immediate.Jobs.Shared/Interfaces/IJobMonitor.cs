using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	Read-only single-job monitoring.
/// </summary>
public interface IJobMonitor
{
	/// <summary>
	/// 	Gets one job and its incoming dependencies.
	/// </summary>
	/// <param name="jobId">
	/// 	The invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The job status, or <see langword="null"/> when the invocation does not exist.
	/// </returns>
	ValueTask<JobStatus?> GetJobAsync(string jobId, CancellationToken cancellationToken = default);
}

using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Storage-backed implementation of the public monitoring services.
/// </summary>
/// <param name="storage">
/// 	The storage provider queried for job and batch status.
/// </param>
/// <param name="definitions">
/// 	The generated job definitions used to enrich monitoring results.
/// </param>
public sealed class JobMonitor(IJobStorage storage, IEnumerable<JobDefinition> definitions) : IBatchMonitor, IJobMonitor
{
	/// <inheritdoc />
	public ValueTask<BatchStatus?> GetStatusAsync(string batchId, CancellationToken cancellationToken = default) =>
		JobStorageCapabilityGuards.RequireGraph(storage).GetBatchStatusAsync(batchId, cancellationToken);

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<BatchMemberStatus>> QueryMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	) => JobStorageCapabilityGuards.RequireGraph(storage).QueryBatchMembersAsync(batchId, query, cancellationToken);

	/// <inheritdoc />
	public ValueTask<BatchGraph?> GetGraphAsync(string batchId, CancellationToken cancellationToken = default) =>
		JobStorageCapabilityGuards.RequireGraph(storage).GetBatchGraphAsync(batchId, cancellationToken);

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
	{
		var status = await storage.GetJobStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
		if (status is null)
			return null;
		var definition = definitions.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, status.JobName, StringComparison.Ordinal));
		return definition is null ? status : status with { MaxAttempts = definition.MaxAttempts };
	}
}

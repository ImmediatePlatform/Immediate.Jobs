using Immediate.Jobs.Shared.Interfaces;
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
public sealed class JobMonitor(IJobStorage storage, IEnumerable<JobDefinition> definitions) : IJobMonitor
{
	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return snapshot with { Capabilities = storage.GetCapabilities() };
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	) => storage.QueryJobsAsync(query, cancellationToken);

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<JobExecutionRecord>> QueryExecutionsAsync(
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	) => storage.QueryJobExecutionsAsync(query, cancellationToken);

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	) => storage is IJobGraphStorage graphStorage
		? graphStorage.QueryBatchesAsync(query, cancellationToken)
		: ValueTask.FromResult<IReadOnlyList<BatchStatus>>([]);

	/// <inheritdoc />
	public ValueTask<BatchStatus?> GetBatchAsync(BatchHandle batch, CancellationToken cancellationToken = default) =>
		storage is IJobGraphStorage graphStorage
			? graphStorage.GetBatchStatusAsync(batch, cancellationToken)
			: ValueTask.FromResult<BatchStatus?>(null);

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		BatchHandle batch,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	) => storage is IJobGraphStorage graphStorage
		? graphStorage.QueryBatchMembersAsync(batch, query, cancellationToken)
		: ValueTask.FromResult<IReadOnlyList<BatchMemberStatus>>([]);

	/// <inheritdoc />
	public ValueTask<BatchGraph?> GetBatchGraphAsync(BatchHandle batch, CancellationToken cancellationToken = default) =>
		storage is IJobGraphStorage graphStorage
			? graphStorage.GetBatchGraphAsync(batch, cancellationToken)
			: ValueTask.FromResult<BatchGraph?>(null);

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobAsync(JobHandle job, CancellationToken cancellationToken = default)
	{
		var status = await storage.GetJobStatusAsync(job, cancellationToken).ConfigureAwait(false);
		if (status is null)
			return null;
		var definition = definitions.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, status.JobName, StringComparison.Ordinal));
		return definition is null ? status : status with { MaxAttempts = definition.MaxAttempts };
	}
}

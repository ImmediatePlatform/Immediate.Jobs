using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Validations.Shared;

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
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return await storage.QueryJobsAsync(query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryExecutionsAsync(
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return await storage.QueryJobExecutionsAsync(query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>?> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return storage switch
		{
			IJobGraphStorage graphStorage => await graphStorage.QueryBatchesAsync(query, cancellationToken),
			_ => null,
		};
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(batchId);

		return storage switch
		{
			IJobGraphStorage graphStorage => await graphStorage.GetBatchStatusAsync(batchId, cancellationToken),
			_ => null,
		};
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>?> QueryBatchMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(batchId);
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return storage switch
		{
			IJobGraphStorage graphStorage => await graphStorage.QueryBatchMembersAsync(batchId, query, cancellationToken),
			_ => null,
		};
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(string batchId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(batchId);

		return storage switch
		{
			IJobGraphStorage graphStorage => await graphStorage.GetBatchGraphAsync(batchId, cancellationToken),
			_ => null,
		};
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(jobId);

		var status = await storage.GetJobStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
		if (status is null)
			return null;

		var definition = definitions.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, status.JobName, StringComparison.Ordinal)
		);

		return definition is null ? status : status with { MaxAttempts = definition.MaxAttempts };
	}
}

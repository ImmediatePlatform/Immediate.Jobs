using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Storage-backed implementation of the public job monitoring and management services.
/// </summary>
/// <param name="storage">
/// 	The storage provider queried for job and batch status.
/// </param>
/// <param name="definitions">
/// 	The generated job definitions used to enrich monitoring results.
/// </param>
/// <param name="timeProvider">
/// 	The clock used when triggering recurring jobs.
/// </param>
/// <param name="idGenerator">
/// 	The identifier generator used when triggering recurring jobs.
/// </param>
public sealed class JobMonitor(
	IJobStorage storage,
	IEnumerable<JobDefinition> definitions,
	TimeProvider timeProvider,
	IIdGenerator idGenerator
) : IJobMonitor
{
	private readonly Dictionary<string, JobDefinition> _definitionsByName = definitions.ToDictionary(x => x.Name, StringComparer.Ordinal);

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
		await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);

	/// <summary>Cancels a non-terminal job.</summary>
	/// <param name="jobId">The invocation identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	public ValueTask CancelJobAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(jobId);

		return storage.CancelAsync(jobId, cancellationToken);
	}

	/// <summary>Moves a terminal job back to pending.</summary>
	/// <param name="jobId">The invocation identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	public ValueTask RetryJobAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(jobId);

		return storage.RetryAsync(jobId, cancellationToken);
	}

	/// <summary>Cancels a batch and its non-terminal members.</summary>
	/// <param name="batchId">The batch identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	public async ValueTask CancelBatchAsync(BatchHandle batchId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(batchId);

		if (storage is not IJobGraphStorage graphStorage)
			throw new KeyNotFoundException($"Batch '{batchId}' is not available.");

		await graphStorage.CancelBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Deletes a terminal batch and its retained graph.</summary>
	/// <param name="batchId">The batch identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	public async ValueTask DeleteBatchAsync(BatchHandle batchId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(batchId);

		if (storage is not IJobGraphStorage graphStorage)
			throw new KeyNotFoundException($"Batch '{batchId}' is not available.");

		await graphStorage.DeleteBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Pauses a recurring schedule.</summary>
	/// <param name="name">The recurring schedule name.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		if (storage is not IRecurringJobStorage recurringStorage)
			throw new KeyNotFoundException($"Recurring schedule '{name}' is not available.");

		await recurringStorage.PauseRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Resumes a recurring schedule.</summary>
	/// <param name="name">The recurring schedule name.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		if (storage is not IRecurringJobStorage recurringStorage)
			throw new KeyNotFoundException($"Recurring schedule '{name}' is not available.");

		await recurringStorage.ResumeRecurringAsync(name, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Creates an immediate invocation from a recurring schedule.</summary>
	/// <param name="name">The recurring schedule name.</param>
	/// <param name="cancellationToken">A token that can cancel the operation.</param>
	public async ValueTask TriggerRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

		var schedule = snapshot.Recurring
			.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
			?? throw new KeyNotFoundException($"Recurring schedule '{name}' is not available.");

		if (!_definitionsByName.ContainsKey(schedule.JobName))
			throw new ImmediateJobException($"No generated job definition exists for '{name}'.");

		var now = timeProvider.GetUtcNow();
		await storage.EnqueueAsync(
			new()
			{
				JobId = JobHandle.FromString(idGenerator.CreateId(IdKind.Job)),
				JobName = schedule.JobName,
				QueueName = schedule.QueueName,
				Payload = "{}",
				State = JobState.Pending,
				DueAt = now,
				CreatedAt = now,
			},
			cancellationToken
		).ConfigureAwait(false);
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
		JobHandle jobId,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return await storage.QueryJobExecutionsAsync(jobId, query, cancellationToken);
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
	public async ValueTask<BatchStatus?> GetBatchAsync(BatchHandle batchId, CancellationToken cancellationToken = default)
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
		BatchHandle batchId,
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
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(BatchHandle batchId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(batchId);

		return storage switch
		{
			IJobGraphStorage graphStorage => await graphStorage.GetBatchGraphAsync(batchId, cancellationToken),
			_ => null,
		};
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobAsync(JobHandle jobId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(jobId);

		var status = await storage.GetJobStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
		if (status is null)
			return null;

		if (!_definitionsByName.TryGetValue(status.JobName, out var definition))
			return status;

		return status with { MaxAttempts = definition.MaxAttempts };
	}
}

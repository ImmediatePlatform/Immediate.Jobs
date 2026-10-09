using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Storage;
using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Storage-backed implementation of the public job monitoring and management services.
/// </summary>
/// <param name="storage">
/// 	The storage provider queried for job and batch status.
/// </param>
/// <param name="timeProvider">
/// 	The clock used when triggering recurring jobs.
/// </param>
/// <param name="idGenerator">
/// 	The identifier generator used when triggering recurring jobs.
/// </param>
public sealed class JobMonitor(
	IJobStorage storage,
	TimeProvider timeProvider,
	IIdGenerator idGenerator
) : IJobMonitor
{
	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		return await storage.GetMonitoringSnapshotAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringDefinitions> GetDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		return await storage.GetMonitoringDefinitionsAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask PauseDefinitionAsync(string jobName, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		cancellationToken.ThrowIfCancellationRequested();
		var definition = await GetStoredDefinitionAsync(jobName, cancellationToken);
		await storage.PauseJobAsync(definition.Name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeDefinitionAsync(string jobName, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		cancellationToken.ThrowIfCancellationRequested();
		var definition = await GetStoredDefinitionAsync(jobName, cancellationToken);
		await storage.ResumeJobAsync(definition.Name, definition.AcquisitionLimits, cancellationToken);
	}

	private async ValueTask<JobDefinitionRecord> GetStoredDefinitionAsync(string jobName, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
		return (await storage.GetJobDefinitionsAsync(cancellationToken))
			.FirstOrDefault(definition => string.Equals(definition.Name, jobName, StringComparison.OrdinalIgnoreCase))
			?? throw new KeyNotFoundException($"Job definition '{jobName}' is not stored.");
	}

	/// <summary>
	/// 	Cancels a non-terminal job.
	/// </summary>
	/// <param name="jobHandle">
	/// 	The invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	public async ValueTask CancelJobAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(jobHandle);

		await storage.CancelAsync(jobHandle, cancellationToken);
	}

	/// <summary>
	/// 	Moves a terminal job back to pending.
	/// </summary>
	/// <param name="jobHandle">
	/// 	The invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	public async ValueTask RetryJobAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(jobHandle);

		await storage.RetryAsync(jobHandle, cancellationToken);
	}

	/// <summary>
	/// 	Cancels a batch and its non-terminal members.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	public async ValueTask CancelBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(batchHandle);

		if (storage is not IJobGraphStorage graphStorage)
			throw new KeyNotFoundException($"Batch '{batchHandle}' is not available.");

		await graphStorage.CancelBatchAsync(batchHandle, cancellationToken);
	}

	/// <summary>
	/// 	Deletes a terminal batch and its retained graph.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	public async ValueTask DeleteBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(batchHandle);

		if (storage is not IJobGraphStorage graphStorage)
			throw new KeyNotFoundException($"Batch '{batchHandle}' is not available.");

		await graphStorage.DeleteBatchAsync(batchHandle, cancellationToken);
	}

	/// <summary>
	/// 	Pauses a recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The recurring schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		await storage.PauseRecurringAsync(name, cancellationToken);
	}

	/// <summary>
	/// 	Resumes a recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The recurring schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		await storage.ResumeRecurringAsync(name, cancellationToken);
	}

	/// <summary>
	/// 	Creates an immediate invocation from a recurring schedule.
	/// </summary>
	/// <param name="name">
	/// 	The recurring schedule name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	public async ValueTask TriggerRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		var definitions = await GetDefinitionsAsync(cancellationToken);

		var schedule = definitions.Recurring
			.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
			?? throw new KeyNotFoundException($"Recurring schedule '{name}' is not available.");

		var definition = await GetStoredDefinitionAsync(schedule.JobName, cancellationToken);

		var now = timeProvider.GetUtcNow();
		await storage.EnqueueAsync(
			new()
			{
				JobHandle = JobHandle.FromString(idGenerator.CreateId(IdKind.Job)),
				JobName = definition.Name,
				QueueName = definition.QueueName,
				Payload = "{}",
				State = JobState.Pending,
				DueAt = now,
				CreatedAt = now,
			},
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return await storage.QueryJobsAsync(query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryExecutionsAsync(
		JobHandle jobHandle,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(jobHandle);
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return await storage.QueryJobExecutionsAsync(jobHandle, query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>?> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return storage switch
		{
			IJobGraphStorage graphStorage => await graphStorage.QueryBatchesAsync(query, cancellationToken),
			_ => null,
		};
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(batchHandle);

		return storage switch
		{
			IJobGraphStorage graphStorage => await graphStorage.GetBatchStatusAsync(batchHandle, cancellationToken),
			_ => null,
		};
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>?> QueryBatchMembersAsync(
		BatchHandle batchHandle,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(batchHandle);
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return storage switch
		{
			IJobGraphStorage graphStorage => await graphStorage.QueryBatchMembersAsync(batchHandle, query, cancellationToken),
			_ => null,
		};
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(BatchHandle batchHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(batchHandle);

		return storage switch
		{
			IJobGraphStorage graphStorage => await graphStorage.GetBatchGraphAsync(batchHandle, cancellationToken),
			_ => null,
		};
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobAsync(JobHandle jobHandle, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		ArgumentNullException.ThrowIfNull(jobHandle);

		var status = await storage.GetJobStatusAsync(jobHandle, cancellationToken);
		if (status is null)
			return null;

		var definition = (await storage.GetJobDefinitionsAsync(cancellationToken))
			.FirstOrDefault(definition => string.Equals(definition.Name, status.JobName, StringComparison.OrdinalIgnoreCase));
		return definition is not null ? status with { MaxAttempts = definition.MaxAttempts } : status;
	}
}

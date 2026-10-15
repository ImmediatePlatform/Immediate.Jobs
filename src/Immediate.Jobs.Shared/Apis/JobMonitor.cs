using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Storage;
using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Storage-backed monitoring and management for jobs, executions, batches, schedules, and scheduler nodes.
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
)
{
	/// <summary>
	/// 	Gets the aggregate monitoring snapshot.
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The current aggregate monitoring snapshot.
	/// </returns>
	public async ValueTask<JobMonitoringSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		return await storage.GetMonitoringSnapshotAsync(cancellationToken);
	}

	/// <summary>
	/// 	Gets stored job and recurring configuration without live acquisition state.
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the read.
	/// </param>
	/// <returns>
	/// 	All persisted monitoring definitions.
	/// </returns>
	public async ValueTask<JobMonitoringDefinitions> GetDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		return await storage.GetMonitoringDefinitionsAsync(cancellationToken);
	}

	/// <summary>
	/// 	Pauses acquisition of a stored definition's pending and future jobs. Running work may finish.
	/// </summary>
	/// <param name="jobName">
	/// 	The stable job definition name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	The asynchronous pause operation.
	/// </returns>
	/// <exception cref="KeyNotFoundException">
	/// 	The definition is not stored.
	/// </exception>
	public async ValueTask PauseDefinitionAsync(string jobName, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
		await storage.PauseJobAsync(jobName, cancellationToken);
	}

	/// <summary>
	/// 	Resumes acquisition of a stored definition, preserving its rate counters and configured limits.
	/// </summary>
	/// <param name="jobName">
	/// 	The stable job definition name.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the operation.
	/// </param>
	/// <returns>
	/// 	The asynchronous resume operation.
	/// </returns>
	/// <exception cref="KeyNotFoundException">
	/// 	The definition is not stored.
	/// </exception>
	public async ValueTask ResumeDefinitionAsync(string jobName, CancellationToken cancellationToken = default)
	{
		await TaskScheduler.Yield();
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
		await storage.ResumeJobAsync(jobName, cancellationToken);
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

		var statuses = await GetSnapshotAsync(cancellationToken);
		if (statuses.Recurring.Any(status => string.Equals(status.Name, schedule.Name, StringComparison.Ordinal) && status.CancellationReason is not null))
			throw new ImmediateJobException("Canceled recurring schedules cannot be triggered. Remove the schedule and create it again.");

		var now = timeProvider.GetUtcNow();
		await storage.EnqueueAsync(
			new()
			{
				JobHandle = JobHandle.FromString(idGenerator.CreateId(IdKind.Job)),
				JobName = schedule.JobName,
				Payload = "{}",
				State = JobState.Pending,
				DueAt = now,
				CreatedAt = now,
			},
			cancellationToken
		);
	}

	/// <summary>
	/// 	Returns jobs matching a monitoring query.
	/// </summary>
	/// <param name="query">
	/// 	The job filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The jobs matching the query.
	/// </returns>
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		await TaskScheduler.Yield();
		ValidationException.ThrowIfInvalid(query, $"Invalid argument \"{nameof(query)}\"");

		return await storage.QueryJobsAsync(query, cancellationToken);
	}

	/// <summary>
	/// 	Returns retained executions for one job.
	/// </summary>
	/// <param name="jobHandle">
	/// 	The job identifier.
	/// </param>
	/// <param name="query">
	/// 	The exact-ordinal filter and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The retained executions matching the query.
	/// </returns>
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

	/// <summary>
	/// 	Returns batches matching a monitoring query.
	/// </summary>
	/// <param name="query">
	/// 	The batch filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The batches matching the query, or <see langword="null"/> when the current job storage does not support batches.
	/// </returns>
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

	/// <summary>
	/// 	Gets aggregate progress for one batch.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The aggregate batch status, or <see langword="null"/> when the batch does not exist or the current job storage does not support batches.
	/// </returns>
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

	/// <summary>
	/// 	Queries members of one batch.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The batch identifier.
	/// </param>
	/// <param name="query">
	/// 	The member filters and paging options.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The batch members matching the query, or <see langword="null"/> when the current job storage does not support batches.
	/// </returns>
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

	/// <summary>
	/// 	Gets the persisted dependency graph for one batch.
	/// </summary>
	/// <param name="batchHandle">
	/// 	The batch identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The batch dependency graph, or <see langword="null"/> when the batch does not exist or the current job storage does not support batches.
	/// </returns>
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

	/// <summary>
	/// 	Gets one job and its incoming dependencies.
	/// </summary>
	/// <param name="jobHandle">
	/// 	The invocation identifier.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the monitoring operation.
	/// </param>
	/// <returns>
	/// 	The job status, or <see langword="null"/> when the invocation does not exist.
	/// </returns>
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

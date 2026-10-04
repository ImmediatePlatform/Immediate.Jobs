using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask SetExecutionTelemetryAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		string? traceId,
		string? spanId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken = default
	)
	{
		SetExecutionTelemetryAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var job = GetOwnedActive(jobHandle, executionNumber, workerId);
			MaterializeSyntheticExecution(job);
			_jobs[jobHandle] = job with
			{
				ExecutionTraceId = traceId,
				ExecutionSpanId = spanId,
				ExecutionStartedAt = startedAt,
			};
			UpdateExecution(jobHandle, executionNumber, execution => execution with
			{
				ExecutionTraceId = traceId,
				ExecutionSpanId = spanId,
				ExecutionStartedAt = startedAt,
			});
		}
	}

	private void CreateExecution(JobRecord job, DateTimeOffset acquiredAt)
	{
		if (!_executions.TryGetValue(job.JobHandle, out var executions))
			_executions.Add(job.JobHandle, executions = []);
		if (!executions.TryAdd(job.Attempt, new()
		{
			JobHandle = job.JobHandle,
			Attempt = job.Attempt,
			State = JobExecutionState.Active,
			WorkerId = job.WorkerId,
			AcquiredAt = acquiredAt,
		}))
		{
			throw new ImmediateJobException(string.Create(
				CultureInfo.InvariantCulture,
				$"Execution {job.Attempt} for job '{job.JobHandle}' already exists."
			));
		}
	}

	private void InterruptExecution(JobRecord job)
	{
		CompleteExecution(job, JobExecutionState.Interrupted, job.LeaseExpiresAt ?? timeProvider.GetUtcNow());
	}

	private void CompleteExecution(
		JobRecord job,
		JobExecutionState state,
		DateTimeOffset completedAt,
		string? error = null
	)
	{
		if (job.Attempt <= 0)
			return;

		MaterializeSyntheticExecution(job);
		UpdateExecution(job.JobHandle, job.Attempt, execution => execution with
		{
			State = state,
			CompletedAt = completedAt,
			Error = error,
		});
	}

	private JobRecord GetOwnedActive(JobHandle jobHandle, int executionNumber, string workerId)
	{
		if (!_jobs.TryGetValue(jobHandle, out var job) || job.State != JobState.Active)
		{
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");
		}

		if (job.Attempt != executionNumber)
		{
			throw new ImmediateJobException(string.Create(
				CultureInfo.InvariantCulture,
				$"Execution {executionNumber} does not own active job '{jobHandle}'; the active execution is {job.Attempt}."
			));
		}

		if (!string.Equals(job.WorkerId, workerId, StringComparison.Ordinal))
			throw new ImmediateJobException($"Worker '{workerId}' does not own active job '{jobHandle}'.");

		return job;
	}

	private void MaterializeSyntheticExecution(JobRecord job)
	{
		var synthetic = JobExecutionRecords.CreateSynthetic(job);
		if (synthetic is null)
			return;
		if (!_executions.TryGetValue(job.JobHandle, out var executions))
			_executions.Add(job.JobHandle, executions = []);
		_ = executions.TryAdd(synthetic.Attempt, synthetic);
	}

	private void UpdateExecution(
		JobHandle jobHandle,
		int executionNumber,
		Func<JobExecutionRecord, JobExecutionRecord> update
	)
	{
		if (!_executions.TryGetValue(jobHandle, out var executions) || !executions.TryGetValue(executionNumber, out var execution))
		{
			throw new ImmediateJobException(string.Create(
				CultureInfo.InvariantCulture,
				$"Execution {executionNumber} for job '{jobHandle}' was not found."
			));
		}

		executions[executionNumber] = update(execution);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InMemorySetExecutionTelemetryAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SetExecutionTelemetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SetExecutionTelemetryAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void SetExecutionTelemetryAsyncCalled(JobHandle jobHandle, int execution);
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
			ref var job = ref GetOwnedActive(jobHandle, executionNumber, workerId);
			MaterializeSyntheticExecution(job);
			job = job with
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

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
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

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void InterruptExecution(JobRecord job)
	{
		CompleteExecution(job, JobExecutionState.Interrupted, job.LeaseExpiresAt ?? timeProvider.GetUtcNow());
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
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

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and while using the returned reference.
	/// 	The dictionary must not be structurally modified while the reference is in use.
	/// </remarks>
	[SuppressMessage("Style", "IDE0007:Use implicit type", Justification = "Interaction between IDE0007 and CS8619")]
	private ref JobRecord GetOwnedActive(JobHandle jobHandle, int executionNumber, string workerId)
	{
		ref JobRecord job = ref CollectionsMarshal.GetValueRefOrNullRef(_jobs, jobHandle);
		if (Unsafe.IsNullRef(ref job) || job.State != JobState.Active)
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

		return ref job;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void MaterializeSyntheticExecution(JobRecord job)
	{
		var synthetic = JobExecutionRecords.CreateSynthetic(job);
		if (synthetic is null)
			return;
		if (!_executions.TryGetValue(job.JobHandle, out var executions))
			_executions.Add(job.JobHandle, executions = []);
		_ = executions.TryAdd(synthetic.Attempt, synthetic);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
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

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemorySetExecutionTelemetryAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SetExecutionTelemetryAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SetExecutionTelemetryAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void SetExecutionTelemetryAsyncCalled(JobHandle jobHandle, int execution);
}

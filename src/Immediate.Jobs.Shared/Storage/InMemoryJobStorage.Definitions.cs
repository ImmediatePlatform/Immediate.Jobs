using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	internal void ApplyAcquiredJobs(IReadOnlyList<JobRecord> acquired)
	{
		lock (_gate)
		{
			foreach (var job in acquired)
			{
				if (!_jobs.TryGetValue(job.JobHandle, out var previous) || job.Attempt != previous.Attempt + 1)
					throw new ImmediateJobException("The single-server primary has drifted from durable storage. Use only one scheduler process.");
				MaterializeSyntheticExecution(previous);
				if (previous.State == JobState.Active)
					InterruptExecution(previous);
				_jobs[job.JobHandle] = job;
				CreateExecution(job, timeProvider.GetUtcNow());
				MarkBatchStarted(job.BatchHandle, timeProvider.GetUtcNow());
			}
		}
	}

	private readonly Dictionary<string, DefinitionAcquisition> _acquisitionDefinitions = [with(StringComparer.Ordinal)];

	/// <inheritdoc />
	public async ValueTask PauseJobAsync(string jobName, CancellationToken cancellationToken = default)
	{
		InMemoryPauseJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		lock (_gate)
		{
			var definition = GetAcquisitionDefinition(jobName);
			definition.State = definition.State with { IsPaused = true, AcquisitionStatus = JobAcquisitionStatus.Paused, NextEligibleAt = null };
		}
	}

	/// <inheritdoc />
	public async ValueTask ResumeJobAsync(string jobName, JobAcquisitionLimits limits, CancellationToken cancellationToken = default)
	{
		InMemoryResumeJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		limits.Validate();
		await TaskScheduler.Yield();
		lock (_gate)
		{
			var definition = GetAcquisitionDefinition(jobName);
			definition.State = definition.State with { IsPaused = false };
			_ = EvaluateAcquisition(jobName, limits, timeProvider.GetUtcNow());
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, JobAcquisitionLimits limits, CancellationToken cancellationToken = default)
	{
		InMemoryGetJobAcquisitionStateAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		limits.Validate();
		await TaskScheduler.Yield();
		lock (_gate)
			return EvaluateAcquisition(jobName, limits, timeProvider.GetUtcNow(), includeActiveCount: true);
	}

	private DefinitionAcquisition GetAcquisitionDefinition(string jobName)
	{
		if (!_acquisitionDefinitions.TryGetValue(jobName, out var definition))
			_acquisitionDefinitions.Add(jobName, definition = new() { State = new() { JobName = jobName } });
		return definition;
	}

	private JobAcquisitionState EvaluateAcquisition(string jobName, JobAcquisitionLimits limits, DateTimeOffset now, bool includeActiveCount = false)
	{
		var definition = GetAcquisitionDefinition(jobName);
		var acquisitions = limits.SlidingWindowPeriod is { } period
			? definition.Acquisitions.Where(acquired => acquired > now - period).Order().ToList()
			: [];
		definition.State = JobAcquisitionEvaluator.Evaluate(
			jobName, definition.State.IsPaused, limits, now, acquisitions,
			definition.FixedWindowStart, definition.FixedWindowCount,
			(includeActiveCount || limits.MaxConcurrency > 0)
				? _jobs.Values.Count(job => string.Equals(job.JobName, jobName, StringComparison.Ordinal) &&
					job.State == JobState.Active && job.LeaseExpiresAt > now)
				: 0
		);
		return definition.State;
	}

	private bool CanAcquire(string jobName, JobAcquisitionRequest request, DateTimeOffset now) =>
		EvaluateAcquisition(jobName, request.LimitsFor(jobName), now).AcquisitionStatus == JobAcquisitionStatus.Ready;

	private void RecordAcquisition(string jobName, JobAcquisitionRequest request, DateTimeOffset now)
	{
		var limits = request.LimitsFor(jobName);
		var definition = GetAcquisitionDefinition(jobName);
		if (limits.SlidingWindowPeriod is { } period)
		{
			_ = definition.Acquisitions.RemoveAll(acquired => acquired <= now - period);
			definition.Acquisitions.Add(now);
		}

		if (limits.FixedWindowStart(now) is { } start)
		{
			if (definition.FixedWindowStart != start)
			{
				definition.FixedWindowStart = start;
				definition.FixedWindowCount = 0;
			}

			definition.FixedWindowCount++;
		}

		_ = EvaluateAcquisition(jobName, limits, now);
	}

	private sealed class DefinitionAcquisition
	{
		public required JobAcquisitionState State { get; set; }
		public List<DateTimeOffset> Acquisitions { get; } = [];
		public DateTimeOffset? FixedWindowStart { get; set; }
		public int FixedWindowCount { get; set; }
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryPauseJobAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryPauseJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseJobAsync called (JobName={JobName})"
	)]
	private partial void InMemoryPauseJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryResumeJobAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryResumeJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeJobAsync called (JobName={JobName})"
	)]
	private partial void InMemoryResumeJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryGetJobAcquisitionStateAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryGetJobAcquisitionStateAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobAcquisitionStateAsync called (JobName={JobName})"
	)]
	private partial void InMemoryGetJobAcquisitionStateAsyncCalled(string jobName);
}

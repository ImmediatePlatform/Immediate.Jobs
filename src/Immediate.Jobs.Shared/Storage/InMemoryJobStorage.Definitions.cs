using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask PauseJobAsync(string jobName, CancellationToken cancellationToken = default)
	{
		InMemoryPauseJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			jobName = GetStoredDefinition(jobName).Name;
			var definition = GetAcquisitionDefinition(jobName);
			definition.State = definition.State with { IsPaused = true, AcquisitionStatus = JobAcquisitionStatus.Paused, NextEligibleAt = null };
		}
	}

	/// <inheritdoc />
	public async ValueTask ResumeJobAsync(string jobName, CancellationToken cancellationToken = default)
	{
		InMemoryResumeJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		lock (_gate)
		{
			var definition = GetStoredDefinition(jobName);
			var acquisition = GetAcquisitionDefinition(definition.Name);
			acquisition.State = acquisition.State with { IsPaused = false };
			EvaluateAcquisition(definition.Name, definition.AcquisitionLimits, timeProvider.GetUtcNow());
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, CancellationToken cancellationToken = default)
	{
		InMemoryGetJobAcquisitionStateAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		lock (_gate)
		{
			var definition = GetStoredDefinition(jobName);
			return EvaluateAcquisition(definition.Name, definition.AcquisitionLimits, timeProvider.GetUtcNow(), includeActiveCount: true);
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private JobDefinitionRecord GetStoredDefinition(string jobName) =>
		_jobDefinitions.TryGetValue(jobName, out var definition)
			? definition
			: throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and any use of the returned mutable state.
	/// </remarks>
	private DefinitionAcquisition GetAcquisitionDefinition(string jobName)
	{
		if (!_acquisitionDefinitions.TryGetValue(jobName, out var definition))
			_acquisitionDefinitions.Add(jobName, definition = new() { State = new() { JobName = jobName } });
		return definition;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
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
				? GetActiveLeaseCount(jobName, now)
				: 0
		);
		return definition.State;
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private bool CanAcquire(string jobName, DateTimeOffset now) =>
		EvaluateAcquisition(jobName, GetStoredDefinition(jobName).AcquisitionLimits, now).AcquisitionStatus == JobAcquisitionStatus.Ready;

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void RecordAcquisition(string jobName, DateTimeOffset now)
	{
		var limits = GetStoredDefinition(jobName).AcquisitionLimits;
		var definition = GetAcquisitionDefinition(jobName);
		if (limits.SlidingWindowPeriod is { } period)
		{
			definition.Acquisitions.RemoveAll(acquired => acquired <= now - period);
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

		EvaluateAcquisition(jobName, limits, now);
	}

	private sealed class DefinitionAcquisition
	{
		public required JobAcquisitionState State { get; set; }
		public List<DateTimeOffset> Acquisitions { get; } = [];
		public DateTimeOffset? FixedWindowStart { get; set; }
		public int FixedWindowCount { get; set; }
	}

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryPauseJobAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryPauseJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseJobAsync called (JobName={JobName})"
	)]
	private partial void InMemoryPauseJobAsyncCalled(string jobName);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryResumeJobAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryResumeJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeJobAsync called (JobName={JobName})"
	)]
	private partial void InMemoryResumeJobAsyncCalled(string jobName);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryGetJobAcquisitionStateAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InMemoryGetJobAcquisitionStateAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobAcquisitionStateAsync called (JobName={JobName})"
	)]
	private partial void InMemoryGetJobAcquisitionStateAsyncCalled(string jobName);
}

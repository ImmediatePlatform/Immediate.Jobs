namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Evaluates definition-wide acquisition limits from storage-neutral counters and timestamps.
/// </summary>
public static class JobAcquisitionEvaluator
{
	/// <summary>
	/// 	Computes the acquisition status for a job definition using its current stored usage.
	/// </summary>
	/// <param name="jobName">
	/// 	The stable job definition name.
	/// </param>
	/// <param name="isPaused">
	/// 	Whether acquisition of this definition is explicitly paused.
	/// </param>
	/// <param name="limits">
	/// 	The definition's parsed acquisition limits.
	/// </param>
	/// <param name="now">
	/// 	The current time supplied by the storage provider's clock.
	/// </param>
	/// <param name="slidingAcquisitions">
	/// 	Acquisitions in the current sliding window, ordered from oldest to newest.
	/// </param>
	/// <param name="fixedWindowStart">
	/// 	The beginning of the stored fixed window, or <see langword="null"/> when none is recorded.
	/// </param>
	/// <param name="fixedWindowCount">
	/// 	The number of acquisitions recorded in the stored fixed window.
	/// </param>
	/// <param name="activeCount">
	/// 	The number of active jobs with unexpired execution leases for this definition.
	/// </param>
	/// <returns>
	/// 	The highest-priority acquisition restriction and the next time-based eligibility boundary.
	/// </returns>
	/// <exception cref="ArgumentException">
	/// 	The acquisition limits are invalid.
	/// </exception>
	public static JobAcquisitionState Evaluate(
		string jobName,
		bool isPaused,
		JobAcquisitionLimits limits,
		DateTimeOffset now,
		IReadOnlyList<DateTimeOffset> slidingAcquisitions,
		DateTimeOffset? fixedWindowStart,
		int fixedWindowCount,
		int activeCount
	)
	{
		ArgumentNullException.ThrowIfNull(limits);
		ArgumentNullException.ThrowIfNull(slidingAcquisitions);
		limits.Validate();

		DateTimeOffset? next = null;
		if (limits.SlidingWindowPeriod is { } sliding && slidingAcquisitions.Count >= limits.SlidingWindowMax)
			next = slidingAcquisitions[slidingAcquisitions.Count - limits.SlidingWindowMax] + sliding;
		if (limits.FixedWindowPeriod is { } fixedPeriod && fixedWindowStart == limits.FixedWindowStart(now) && fixedWindowCount >= limits.FixedWindowMax)
		{
			var fixedNext = fixedWindowStart!.Value + fixedPeriod;
			if (next is null || fixedNext > next)
				next = fixedNext;
		}

		var concurrencyLimited = limits.MaxConcurrency > 0 && activeCount >= limits.MaxConcurrency;
		return new()
		{
			JobName = jobName,
			IsPaused = isPaused,
			ActiveCount = activeCount,
			IsConcurrencyLimited = concurrencyLimited,
			AcquisitionStatus = isPaused ? JobAcquisitionStatus.Paused
				: next is not null ? JobAcquisitionStatus.RateLimited
				: concurrencyLimited ? JobAcquisitionStatus.ConcurrencyLimited
				: JobAcquisitionStatus.Ready,
			NextEligibleAt = isPaused || concurrencyLimited ? null : next,
		};
	}
}

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Definition-wide limits shared by every worker acquiring the same job name.
/// </summary>
public sealed record JobAcquisitionLimits
{
	/// <summary>
	/// 	Maximum acquisitions in the preceding sliding period. Zero disables the limit.
	/// </summary>
	public int SlidingWindowMax { get; init; }
	/// <summary>
	/// 	The sliding period, or <see langword="null"/> when disabled.
	/// </summary>
	public TimeSpan? SlidingWindowPeriod { get; init; }
	/// <summary>
	/// 	Maximum acquisitions in each UTC-aligned fixed period. Zero disables the limit.
	/// </summary>
	public int FixedWindowMax { get; init; }
	/// <summary>
	/// 	The fixed period, or <see langword="null"/> when disabled.
	/// </summary>
	public TimeSpan? FixedWindowPeriod { get; init; }
	/// <summary>
	/// 	Maximum unexpired active execution leases. Zero disables the limit.
	/// </summary>
	public int MaxConcurrency { get; init; }

	/// <summary>
	/// 	Validates positive, paired window settings and nonnegative concurrency.
	/// </summary>
	/// <exception cref="ArgumentException">
	/// 	A limit is negative, incomplete, or has a nonpositive period.
	/// </exception>
	public void Validate()
	{
		ValidateWindow(SlidingWindowMax, SlidingWindowPeriod, nameof(SlidingWindowMax));
		ValidateWindow(FixedWindowMax, FixedWindowPeriod, nameof(FixedWindowMax));
		ValidateConcurrency(MaxConcurrency);
	}

	private static void ValidateConcurrency(int maximum)
	{
		if (maximum < 0)
			throw new ArgumentException("MaxConcurrency must not be negative.", nameof(maximum));
	}

	private static void ValidateWindow(int maximum, TimeSpan? period, string parameter)
	{
		if (maximum < 0 || (maximum == 0 ? period is not null : period is null || period <= TimeSpan.Zero))
			throw new ArgumentException("A window must have both a positive maximum and period, or neither.", parameter);
	}

	/// <summary>
	/// 	Returns the UTC-aligned beginning of the fixed window containing the supplied time.
	/// </summary>
	/// <param name="now">
	/// 	The time whose fixed window is required.
	/// </param>
	/// <returns>
	/// 	The fixed-window start, or <see langword="null"/> when the fixed-window limit is disabled.
	/// </returns>
	/// <remarks>
	/// 	Call <see cref="Validate"/> before evaluating windows from an unvalidated configuration.
	/// </remarks>
	public DateTimeOffset? FixedWindowStart(DateTimeOffset now) => FixedWindowPeriod is { } period
		? new DateTimeOffset(now.UtcTicks - now.UtcTicks % period.Ticks, TimeSpan.Zero)
		: null;
}

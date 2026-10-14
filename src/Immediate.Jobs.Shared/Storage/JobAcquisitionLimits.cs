using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Definition-wide limits shared by every worker acquiring the same job name.
/// </summary>
[Validate]
public sealed partial record JobAcquisitionLimits : IValidationTarget<JobAcquisitionLimits>
{
	/// <summary>
	/// 	Maximum acquisitions in the preceding sliding period. Zero disables the limit.
	/// </summary>
	[GreaterThanOrEqual(0)]
	public int SlidingWindowMax { get; init; }

	/// <summary>
	/// 	The sliding period, or <see langword="null"/> when disabled.
	/// </summary>
	[GreaterThan(nameof(TimeSpan.Zero))]
	public TimeSpan? SlidingWindowPeriod { get; init; }

	/// <summary>
	/// 	Maximum acquisitions in each UTC-aligned fixed period. Zero disables the limit.
	/// </summary>
	[GreaterThanOrEqual(0)]
	public int FixedWindowMax { get; init; }

	/// <summary>
	/// 	The fixed period, or <see langword="null"/> when disabled.
	/// </summary>
	[GreaterThan(nameof(TimeSpan.Zero))]
	public TimeSpan? FixedWindowPeriod { get; init; }

	/// <summary>
	/// 	Maximum unexpired active execution leases. Zero disables the limit.
	/// </summary>
	[GreaterThanOrEqual(0)]
	public int MaxConcurrency { get; init; }

	private static void AdditionalValidations(ValidationResult errors, JobAcquisitionLimits target)
	{
		if ((target.SlidingWindowMax > 0) != target.SlidingWindowPeriod.HasValue)
		{
			errors.Add(new()
			{
				PropertyName = nameof(SlidingWindowPeriod),
				ErrorMessage = "SlidingWindowMax and SlidingWindowPeriod must both be provided, or both omitted.",
			});
		}

		if ((target.FixedWindowMax > 0) != target.FixedWindowPeriod.HasValue)
		{
			errors.Add(new()
			{
				PropertyName = nameof(FixedWindowPeriod),
				ErrorMessage = "FixedWindowMax and FixedWindowPeriod must both be provided, or both omitted.",
			});
		}
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
	/// 	Validate the acquisition limits before evaluating windows from an unvalidated configuration.
	/// </remarks>
	public DateTimeOffset? FixedWindowStart(DateTimeOffset now) => FixedWindowPeriod is { } period
		? new DateTimeOffset(now.UtcTicks - now.UtcTicks % period.Ticks, TimeSpan.Zero)
		: null;
}

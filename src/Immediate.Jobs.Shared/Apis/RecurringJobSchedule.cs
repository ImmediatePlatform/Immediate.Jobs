namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A persisted recurring schedule.
/// </summary>
public sealed record RecurringJobSchedule
{
	/// <summary>
	/// 	Unique schedule identity.
	/// </summary>
	/// <value>
	/// 	The unique schedule name.
	/// </value>
	public required string Name { get; init; }

	/// <summary>
	/// 	The generated job definition name.
	/// </summary>
	/// <value>
	/// 	The generated job definition name.
	/// </value>
	public required string JobName { get; init; }

	/// <summary>
	/// 	A five- or six-field cron expression.
	/// </summary>
	/// <value>
	/// 	The recurring cron expression.
	/// </value>
	public required string Cron { get; init; }

	/// <summary>
	/// 	An IANA time-zone identifier.
	/// </summary>
	/// <value>
	/// 	The IANA time-zone identifier.
	/// </value>
	public required string TimeZone { get; init; }

	/// <summary>
	/// 	Whether this schedule originated in compiled code.
	/// </summary>
	/// <value><see langword="true"/> for a code-defined schedule; otherwise, <see langword="false"/>.
	/// </value>
	public required bool IsCodeDefined { get; init; }

	/// <summary>
	/// 	Whether future scheduled occurrences are paused.
	/// </summary>
	/// <value><see langword="true"/> when future occurrences are paused; otherwise, <see langword="false"/>.
	/// </value>
	public bool IsPaused { get; init; }

	/// <summary>
	/// 	The next scheduled occurrence in UTC.
	/// </summary>
	/// <value>
	/// 	The next scheduled occurrence in UTC.
	/// </value>
	public required DateTimeOffset NextRunAt { get; init; }

	/// <summary>
	/// 	The most recently materialized scheduled occurrence in UTC.
	/// </summary>
	/// <value>
	/// 	The latest materialized occurrence in UTC, if any.
	/// </value>
	public DateTimeOffset? LastRunAt { get; init; }
}

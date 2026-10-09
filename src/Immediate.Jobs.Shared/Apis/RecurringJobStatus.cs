namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Live progress for a recurring schedule, without its configuration.
/// </summary>
public sealed record RecurringJobStatus
{
	/// <summary>
	/// 	The schedule identity.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// 	Whether future scheduled occurrences are paused.
	/// </summary>
	/// <value>
	///		<see langword="true"/> when future occurrences are paused; otherwise, <see langword="false"/>.
	/// </value>
	public bool IsPaused { get; init; }

	/// <summary>
	/// 	The next scheduled occurrence in UTC.
	/// </summary>
	public required DateTimeOffset NextRunAt { get; init; }

	/// <summary>
	/// 	The most recently materialized scheduled occurrence in UTC.
	/// </summary>
	public DateTimeOffset? LastRunAt { get; init; }
}

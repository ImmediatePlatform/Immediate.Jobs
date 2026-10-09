namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	Configuration for a persisted recurring schedule.
/// </summary>
public sealed record RecurringJobDefinition
{
	/// <summary>
	/// 	Unique schedule identity.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// 	The generated job definition name.
	/// </summary>
	public required string JobName { get; init; }

	/// <summary>
	/// 	The persisted queue name.
	/// </summary>
	public required string QueueName { get; init; }

	/// <summary>
	/// 	A five- or six-field cron expression.
	/// </summary>
	public required string Cron { get; init; }

	/// <summary>
	/// 	An IANA time-zone identifier.
	/// </summary>
	public required string TimeZone { get; init; }

	/// <summary>
	/// 	Whether this schedule originated in compiled code.
	/// </summary>
	/// <value>
	///		<see langword="true"/> for a code-defined schedule; otherwise, <see langword="false"/>.
	/// </value>
	public required bool IsCodeDefined { get; init; }

}

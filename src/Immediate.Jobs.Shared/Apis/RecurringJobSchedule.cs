namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A persisted recurring schedule.
/// </summary>
public sealed record RecurringJobSchedule
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

	/// <summary>
	/// 	Returns this schedule's configuration without live progress.
	/// </summary>
	/// <returns>The recurring configuration.</returns>
	public RecurringJobDefinition ToDefinition() => new()
	{
		Name = Name,
		JobName = JobName,
		QueueName = QueueName,
		Cron = Cron,
		TimeZone = TimeZone,
		IsCodeDefined = IsCodeDefined,
	};

	/// <summary>
	/// 	Returns this schedule's live progress without configuration.
	/// </summary>
	/// <returns>The recurring status.</returns>
	public RecurringJobStatus ToStatus() => new()
	{
		Name = Name,
		IsPaused = IsPaused,
		NextRunAt = NextRunAt,
		LastRunAt = LastRunAt,
	};

	/// <summary>
	/// 	Combines recurring configuration and live progress for persistence.
	/// </summary>
	/// <param name="definition">The schedule configuration.</param>
	/// <param name="status">The matching live status.</param>
	/// <returns>The complete persisted schedule.</returns>
	public static RecurringJobSchedule FromDefinition(RecurringJobDefinition definition, RecurringJobStatus status)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(status);
		if (!string.Equals(definition.Name, status.Name, StringComparison.Ordinal))
			throw new ArgumentException("Recurring definition and status names must match.", nameof(status));
		return new()
		{
			Name = definition.Name,
			JobName = definition.JobName,
			QueueName = definition.QueueName,
			Cron = definition.Cron,
			TimeZone = definition.TimeZone,
			IsCodeDefined = definition.IsCodeDefined,
			IsPaused = status.IsPaused,
			NextRunAt = status.NextRunAt,
			LastRunAt = status.LastRunAt,
		};
	}
}

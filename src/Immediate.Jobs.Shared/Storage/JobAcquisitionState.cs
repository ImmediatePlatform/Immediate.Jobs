namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	The current definition-wide reason acquisition is allowed or blocked.
/// </summary>
public enum JobAcquisitionStatus
{
	/// <summary>
	/// 	Definition controls permit acquisition; individual job eligibility still applies.
	/// </summary>
	Ready = 0,

	/// <summary>
	/// 	The definition is explicitly paused.
	/// </summary>
	Paused = 1,

	/// <summary>
	/// 	At least one time-based acquisition limit is exhausted.
	/// </summary>
	RateLimited = 2,

	/// <summary>
	/// 	The maximum number of active execution leases has been reached.
	/// </summary>
	ConcurrencyLimited = 3,
}

/// <summary>
/// 	An acquisition snapshot for one persisted job name, separate from invocation lifecycle.
/// </summary>
public sealed record JobAcquisitionState
{
	/// <summary>
	/// 	The stable job definition name.
	/// </summary>
	public required string JobName { get; init; }

	/// <summary>
	/// 	Whether execution of pending and future invocations is explicitly paused.
	/// </summary>
	public bool IsPaused { get; init; }

	/// <summary>
	/// 	The highest-priority acquisition restriction.
	/// </summary>
	public JobAcquisitionStatus AcquisitionStatus { get; init; }

	/// <summary>
	/// 	When all time-based limits permit acquisition; absent for a pause or concurrency restriction.
	/// </summary>
	public DateTimeOffset? NextEligibleAt { get; init; }

	/// <summary>
	/// 	The number of active jobs with unexpired execution leases, including when concurrency is unbounded.
	/// </summary>
	public int ActiveCount { get; init; }

	/// <summary>
	/// 	Whether the concurrency limit is exhausted, independently of pause or time-based restrictions.
	/// </summary>
	public bool IsConcurrencyLimited { get; init; }
}

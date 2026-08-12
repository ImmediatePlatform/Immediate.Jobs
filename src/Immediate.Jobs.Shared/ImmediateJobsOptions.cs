using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Global scheduler and worker options.
/// </summary>
[Validate]
public sealed partial class ImmediateJobsOptions : IValidationTarget<ImmediateJobsOptions>
{
	/// <summary>
	///     Maximum concurrently executing jobs on this node.
	/// </summary>
	/// <remarks>
	///	    The default exceeds the core count because jobs are typically IO-bound. The practical ceiling is usually the
	///     database connection pool: each executing job holds a service scope, and most jobs hold one pooled connection
	///     for their duration.
	/// </remarks>
	[GreaterThan(0)]
	public int MaxParallelJobs { get; set; } = Math.Clamp(Environment.ProcessorCount * 4, 8, 32);

	/// <summary>
	/// 	Maximum number claimed in one storage round-trip.
	/// </summary>
	[GreaterThan(0)]
	public int AcquisitionBatchSize { get; set; } = 32;

	/// <summary>
	/// 	Fallback interval between storage polls.
	/// </summary>
	[GreaterThan(nameof(TimeSpan.Zero))]
	public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

	/// <summary>
	/// 	Duration of an acquired job lease.
	/// </summary>
	[GreaterThan(nameof(TimeSpan.Zero))]
	public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// 	Maximum time allowed for workers to drain during shutdown.
	/// </summary>
	[GreaterThan(nameof(TimeSpan.Zero))]
	public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// 	Retention for successful history.
	/// </summary>
	[GreaterThanOrEqual(nameof(TimeSpan.Zero))]
	public TimeSpan SucceededRetention { get; set; } = TimeSpan.FromHours(24);

	/// <summary>
	/// 	Retention for failed history.
	/// </summary>
	[GreaterThanOrEqual(nameof(TimeSpan.Zero))]
	public TimeSpan FailedRetention { get; set; } = TimeSpan.FromDays(7);

	/// <summary>
	/// 	Retention for successful batches and all of their members and edges.
	/// </summary>
	[GreaterThanOrEqual(nameof(TimeSpan.Zero))]
	public TimeSpan BatchSucceededRetention { get; set; } = TimeSpan.FromHours(24);

	/// <summary>
	/// 	Retention for failed or cancelled batches and all of their members and edges.
	/// </summary>
	[GreaterThanOrEqual(nameof(TimeSpan.Zero))]
	public TimeSpan BatchFailedRetention { get; set; } = TimeSpan.FromDays(7);

	/// <summary>
	/// 	How frequently terminal history is purged.
	/// </summary>
	[GreaterThan(nameof(TimeSpan.Zero))]
	public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);
}

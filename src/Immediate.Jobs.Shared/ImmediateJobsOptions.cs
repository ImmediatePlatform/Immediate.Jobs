using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Process-wide scheduler and worker options.
/// </summary>
[Validate]
public sealed partial class ImmediateJobsOptions : IValidationTarget<ImmediateJobsOptions>
{
	/// <summary>
	///		Controls whether the scheduling service and it's attendant workers are enabled.
	/// </summary>
	public bool IsJobSchedulingServiceEnabled { get; set; } = true;

	/// <summary>
	///     The number of workers created on this node to handle jobs concurrently. This becomes the maximum number of
	///     concurrently executing jobs on this node.
	/// </summary>
	/// <remarks>
	///	    The default exceeds the core count because jobs are typically IO-bound. The practical ceiling is usually the
	///     database connection pool: each executing job holds a service scope, and most jobs hold one pooled connection
	///     for their duration.
	/// </remarks>
	[GreaterThan(0)]
	public int WorkerCount { get; set; } = Math.Clamp(Environment.ProcessorCount * 4, 8, 32);

	/// <summary>
	/// 	Maximum number claimed in one storage round-trip.
	/// </summary>
	[GreaterThan(0)]
	public int AcquisitionBatchSize { get; set; } = 32;

	/// <summary>
	///     Maximum number of jobs acquired by the scheduling service at any point in time.
	/// </summary>
	/// <remarks>
	/// <para>
	///	    The queue length includes total jobs currently processing plus the number of jobs acquired and waiting to be
	///     processed. This property exists separately to <see cref="WorkerCount"/> to allow jobs to be waiting in
	///     the queue in-between polling intervals. For distributed systems where the consumer may want to increase the
	///     <see cref="PollingInterval"/> to reduce database overhead, this allows involved systems to pre-fill the
	///     queue of waiting jobs to remain busy.
	/// </para>
	/// <para>
	///	    NB: If this value is less than <see cref="WorkerCount"/>, then the effective max parallel jobs is <see
	///     cref="MaxQueueLength"/>.
	/// </para>
	/// </remarks>
	[GreaterThan(0)]
	public int MaxQueueLength { get; set; } = Math.Clamp(Environment.ProcessorCount * 4, 8, 32);

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

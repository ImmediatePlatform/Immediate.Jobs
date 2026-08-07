using Immediate.Jobs.Shared.Interfaces;

namespace Immediate.Jobs.Testing;

/// <summary>
/// A storage-free implementation of <see cref="IJobScheduler{TPayload}"/> that records scheduled calls.
/// </summary>
/// <typeparam name="TPayload">The job payload type.</typeparam>
/// <param name="timeProvider">The optional clock used to determine immediate and delayed due times.</param>
public class CaptureOnlyJobScheduler<TPayload>(TimeProvider? timeProvider = null) : IJobScheduler<TPayload>
{
	private readonly List<ScheduledJobCapture<TPayload>> _captures = [];
	private readonly HashSet<string> _cancelledIds = new(StringComparer.Ordinal);
	private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

	/// <summary>All calls captured in call order.</summary>
	/// <value>The captured scheduler calls.</value>
	public IReadOnlyList<ScheduledJobCapture<TPayload>> Captures => _captures;

	/// <summary>The latest captured call, or <see langword="null"/> when none exists.</summary>
	/// <value>The latest captured call, or <see langword="null"/>.</value>
	public ScheduledJobCapture<TPayload>? Last => _captures.Count == 0 ? null : _captures[^1];

	/// <summary>The identifiers explicitly cancelled through this scheduler.</summary>
	/// <value>The cancelled invocation identifiers.</value>
	public IReadOnlySet<string> CancelledIds => _cancelledIds;

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> EnqueueAsync(TPayload payload, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> EnqueueAsync(TPayload payload, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(TPayload payload, DateTimeOffset at, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		DateTimeOffset at,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, JobHandle job, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, JobHandle job, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, JobHandle job, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		JobHandle job,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<JobHandle> jobs, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<JobHandle> jobs, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<JobHandle> jobs, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<JobHandle> jobs,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, BatchHandle batch, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, BatchHandle batch, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, BatchHandle batch, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		BatchHandle batch,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<BatchHandle> batches, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<BatchHandle> batches, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<BatchHandle> batches, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<BatchHandle> batches,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle Enqueue(TPayload payload, Batch batch) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle Enqueue(TPayload payload, Batch batch, string groupId) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay, string groupId) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at, string groupId) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, BatchJobHandle job, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, BatchJobHandle job, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, BatchJobHandle job, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, IReadOnlyList<BatchJobHandle> jobs, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, IReadOnlyList<BatchJobHandle> jobs, string groupId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, IReadOnlyList<BatchJobHandle> jobs, TimeSpan delay, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	) => throw new NotImplementedException();

	/// <inheritdoc />
	public virtual ValueTask CancelAsync(JobHandle job, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException();

	/// <summary>Clears every captured call and cancellation.</summary>
	public void Clear()
	{
		_captures.Clear();
		_cancelledIds.Clear();
	}

	/// <summary>Creates invocation identifiers. Override when a test requires predictable identifiers.</summary>
	/// <returns>A new invocation identifier.</returns>
	protected virtual string CreateId() => Guid.NewGuid().ToString("N");
}

/// <summary>A captured typed scheduler call.</summary>
/// <typeparam name="TPayload">The captured payload type.</typeparam>
/// <param name="Id">The captured invocation identifier.</param>
/// <param name="Payload">The captured payload.</param>
/// <param name="RunAt">The captured absolute due time.</param>
public sealed record ScheduledJobCapture<TPayload>(string Id, TPayload Payload, DateTimeOffset RunAt)
{
	/// <summary>The normalized fair queue group id supplied to the scheduler.</summary>
	/// <value>The normalized group identifier, or <see langword="null"/>.</value>
	public string? GroupId { get; init; }
}

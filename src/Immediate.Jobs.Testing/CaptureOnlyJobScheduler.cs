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
	private readonly HashSet<string> _cancelledIds = [with(StringComparer.Ordinal)];
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
	public virtual ValueTask<JobHandle> EnqueueAsync(TPayload payload, CancellationToken cancellationToken = default)
	{
		return CaptureAsync(payload, _timeProvider.GetUtcNow(), groupId: null, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> EnqueueAsync(TPayload payload, string groupId, CancellationToken cancellationToken = default)
	{
		return CaptureAsync(payload, _timeProvider.GetUtcNow(), groupId, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> EnqueueAsync(TPayload payload, JobDetails currentJob, ContinuationOptions options = ContinuationOptions.BeforeContinuations, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> EnqueueAsync(TPayload payload, JobDetails currentJob, string groupId, ContinuationOptions options = ContinuationOptions.BeforeContinuations, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(TPayload payload, TimeSpan delay, CancellationToken cancellationToken = default)
	{
		return CaptureDelayedAsync(payload, delay, groupId: null, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		TimeSpan delay,
		string groupId,
		CancellationToken cancellationToken = default
	)
	{
		return CaptureDelayedAsync(payload, delay, groupId, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(TPayload payload, JobDetails currentJob, TimeSpan delay, ContinuationOptions options = ContinuationOptions.BeforeContinuations, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(TPayload payload, JobDetails currentJob, TimeSpan delay, string groupId, ContinuationOptions options = ContinuationOptions.BeforeContinuations, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(TPayload payload, DateTimeOffset at, CancellationToken cancellationToken = default)
	{
		return CaptureAtAsync(payload, at, groupId: null, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		DateTimeOffset at,
		string groupId,
		CancellationToken cancellationToken = default
	)
	{
		return CaptureAtAsync(payload, at, groupId, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(TPayload payload, JobDetails currentJob, DateTimeOffset at, ContinuationOptions options = ContinuationOptions.BeforeContinuations, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(TPayload payload, JobDetails currentJob, DateTimeOffset at, string groupId, ContinuationOptions options = ContinuationOptions.BeforeContinuations, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, ContinuationHandle parent, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		return CaptureAsync(payload, _timeProvider.GetUtcNow(), groupId: null, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, ContinuationHandle parent, string groupId, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		return CaptureAsync(payload, _timeProvider.GetUtcNow(), groupId, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, ContinuationHandle parent, TimeSpan delay, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		return CaptureDelayedAsync(payload, delay, groupId: null, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		ContinuationHandle parent,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default
	)
	{
		return CaptureDelayedAsync(payload, delay, groupId, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<ContinuationHandle> parents, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		return CaptureAsync(payload, _timeProvider.GetUtcNow(), groupId: null, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<ContinuationHandle> parents, string groupId, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		return CaptureAsync(payload, _timeProvider.GetUtcNow(), groupId, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(TPayload payload, IReadOnlyList<ContinuationHandle> parents, TimeSpan delay, ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default)
	{
		return CaptureDelayedAsync(payload, delay, groupId: null, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAfterAsync(
		TPayload payload,
		IReadOnlyList<ContinuationHandle> parents,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success, CancellationToken cancellationToken = default
	)
	{
		return CaptureDelayedAsync(payload, delay, groupId, cancellationToken);
	}

	/// <inheritdoc />
	public virtual JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	)
	{
		return Capture(payload, _timeProvider.GetUtcNow(), groupId: null);
	}

	/// <inheritdoc />
	public virtual JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	)
	{
		return Capture(payload, _timeProvider.GetUtcNow(), groupId);
	}

	/// <inheritdoc />
	public virtual JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	)
	{
		return CaptureDelayed(payload, delay, groupId: null);
	}

	/// <inheritdoc />
	public virtual JobHandle ScheduleAfter(
		TPayload payload,
		JobDetails currentJob,
		TimeSpan delay,
		string groupId,
		ContinuationOptions options = ContinuationOptions.BeforeContinuations
	)
	{
		return CaptureDelayed(payload, delay, groupId);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle Enqueue(TPayload payload, Batch batch)
	{
		return CaptureBatch(payload, batch, _timeProvider.GetUtcNow(), groupId: null);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle Enqueue(TPayload payload, Batch batch, string groupId)
	{
		return CaptureBatch(payload, batch, _timeProvider.GetUtcNow(), groupId);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay)
	{
		return CaptureBatchDelayed(payload, batch, delay, groupId: null);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle Schedule(TPayload payload, Batch batch, TimeSpan delay, string groupId)
	{
		return CaptureBatchDelayed(payload, batch, delay, groupId);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at)
	{
		return CaptureBatchAt(payload, batch, at, groupId: null);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle Schedule(TPayload payload, Batch batch, DateTimeOffset at, string groupId)
	{
		return CaptureBatchAt(payload, batch, at, groupId);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, BatchJobHandle job, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(job);
		return CaptureBatch(payload, job.Batch, _timeProvider.GetUtcNow(), groupId: null);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, BatchJobHandle job, string groupId, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(job);
		return CaptureBatch(payload, job.Batch, _timeProvider.GetUtcNow(), groupId: null);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, BatchJobHandle job, TimeSpan delay, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(job);
		return CaptureBatchDelayed(payload, job.Batch, delay, groupId: null);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(
		TPayload payload,
		BatchJobHandle job,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		ArgumentNullException.ThrowIfNull(job);
		return CaptureBatchDelayed(payload, job.Batch, delay, groupId);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, IReadOnlyList<BatchJobHandle> jobs, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		return CaptureBatch(payload, FirstBatch(jobs), _timeProvider.GetUtcNow(), groupId: null);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, IReadOnlyList<BatchJobHandle> jobs, string groupId, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		return CaptureBatch(payload, FirstBatch(jobs), _timeProvider.GetUtcNow(), groupId);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(TPayload payload, IReadOnlyList<BatchJobHandle> jobs, TimeSpan delay, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		return CaptureBatchDelayed(payload, FirstBatch(jobs), delay, groupId: null);
	}

	/// <inheritdoc />
	public virtual BatchJobHandle ScheduleAfter(
		TPayload payload,
		IReadOnlyList<BatchJobHandle> jobs,
		TimeSpan delay,
		string groupId,
		ContinuationTrigger on = ContinuationTrigger.Success
	)
	{
		return CaptureBatchDelayed(payload, FirstBatch(jobs), delay, groupId);
	}

	/// <inheritdoc />
	public virtual ValueTask CancelAsync(JobHandle job, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(job);
		cancellationToken.ThrowIfCancellationRequested();
		if (!_captures.Any(capture => string.Equals(capture.Id, job.JobId, StringComparison.Ordinal)))
			throw new KeyNotFoundException($"Job '{job.JobId}' was not found.");
		if (!_cancelledIds.Add(job.JobId))
			throw new ImmediateJobException("Only a non-terminal job can be cancelled.");
		return ValueTask.CompletedTask;
	}

	private ValueTask<JobHandle> CaptureDelayedAsync(TPayload payload, TimeSpan delay, string? groupId, CancellationToken cancellationToken)
	{
		ValidateDelay(delay);
		return CaptureAsync(payload, _timeProvider.GetUtcNow() + delay, groupId, cancellationToken);
	}

	private ValueTask<JobHandle> CaptureAtAsync(TPayload payload, DateTimeOffset at, string? groupId, CancellationToken cancellationToken)
	{
		return CaptureAsync(payload, at, groupId, cancellationToken);
	}

	private ValueTask<JobHandle> CaptureAsync(TPayload payload, DateTimeOffset runAt, string? groupId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult(Capture(payload, runAt, groupId));
	}

	private JobHandle CaptureDelayed(TPayload payload, TimeSpan delay, string? groupId)
	{
		ValidateDelay(delay);
		return Capture(payload, _timeProvider.GetUtcNow() + delay, groupId);
	}

	private JobHandle Capture(TPayload payload, DateTimeOffset runAt, string? groupId)
	{
		groupId = NormalizeGroupId(groupId);
		var handle = new JobHandle { JobId = CreateId() };
		_captures.Add(new(handle.JobId, payload, runAt) { GroupId = groupId });
		return handle;
	}

	private BatchJobHandle CaptureBatchDelayed(TPayload payload, Batch batch, TimeSpan delay, string? groupId)
	{
		ValidateDelay(delay);
		return CaptureBatch(payload, batch, _timeProvider.GetUtcNow() + delay, groupId);
	}

	private BatchJobHandle CaptureBatchAt(TPayload payload, Batch batch, DateTimeOffset at, string? groupId)
	{
		return CaptureBatch(payload, batch, at, groupId);
	}

	private BatchJobHandle CaptureBatch(TPayload payload, Batch batch, DateTimeOffset runAt, string? groupId)
	{
		//new() { Batch = batch, JobId = Capture(payload, runAt, groupId) };
		throw new NotImplementedException();
	}

	private static Batch FirstBatch(IReadOnlyList<BatchJobHandle> jobs)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		if (jobs.Count == 0)
			throw new ArgumentException("No prior jobs were provided.", nameof(jobs));
		return jobs[0].Batch;
	}

	private static void ValidateDelay(TimeSpan delay)
	{
		if (delay < TimeSpan.Zero)
			ArgumentOutOfRangeException.Throw(nameof(delay), "A job delay cannot be negative.");
	}

	private static string? NormalizeGroupId(string? groupId)
	{
		if (string.IsNullOrWhiteSpace(groupId))
			return null;
		if (groupId.Length > 128)
			ArgumentException.Throw("A fair queue group id cannot exceed 128 characters.", nameof(groupId));
		return groupId;
	}

	/// <summary>Clears every captured call and cancellation.</summary>
	public void Clear()
	{
		_captures.Clear();
		_cancelledIds.Clear();
	}

	/// <summary>Creates invocation identifiers. Override when a test requires predictable identifiers.</summary>
	/// <returns>A new invocation identifier.</returns>
	protected virtual string CreateId()
	{
		return Guid.NewGuid().ToString("N");
	}
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

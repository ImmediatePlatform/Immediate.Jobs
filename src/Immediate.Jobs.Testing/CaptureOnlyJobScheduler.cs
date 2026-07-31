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
	public virtual ValueTask CancelAsync(JobHandle handle, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(handle.Id, nameof(handle));
		cancellationToken.ThrowIfCancellationRequested();
		if (!_captures.Any(capture => string.Equals(capture.Id, handle.Id, StringComparison.Ordinal)))
			throw new KeyNotFoundException($"Job '{handle.Id}' was not found.");
		if (!_cancelledIds.Add(handle.Id))
			throw new ImmediateJobException("Only a non-terminal job can be cancelled.");
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> EnqueueAsync(TPayload payload, CancellationToken cancellationToken = default) =>
		CaptureAsync(payload, _timeProvider.GetUtcNow(), groupId: null, cancellationToken: cancellationToken);

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> EnqueueAsync(
		TPayload payload,
		string? groupId,
		CancellationToken cancellationToken
	) => CaptureAsync(payload, _timeProvider.GetUtcNow(), groupId, cancellationToken);

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		TimeSpan delay,
		CancellationToken cancellationToken = default
	)
	{
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");
		return CaptureAsync(payload, _timeProvider.GetUtcNow() + delay, groupId: null, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAsync(
		TPayload payload,
		TimeSpan delay,
		string? groupId,
		CancellationToken cancellationToken
	)
	{
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");
		return CaptureAsync(payload, _timeProvider.GetUtcNow() + delay, groupId, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAtAsync(
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken = default
	) => CaptureAsync(payload, runAt, groupId: null, cancellationToken: cancellationToken);

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAtAsync(
		TPayload payload,
		DateTimeOffset runAt,
		string? groupId,
		CancellationToken cancellationToken
	) => CaptureAsync(payload, runAt, groupId, cancellationToken);

	/// <summary>Clears every captured call and cancellation.</summary>
	public void Clear()
	{
		_captures.Clear();
		_cancelledIds.Clear();
	}

	/// <summary>Creates invocation identifiers. Override when a test requires predictable identifiers.</summary>
	/// <returns>A new invocation identifier.</returns>
	protected virtual string CreateId() => Guid.NewGuid().ToString("N");

	private ValueTask<JobHandle> CaptureAsync(
		TPayload payload,
		DateTimeOffset runAt,
		string? groupId,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		groupId = NormalizeGroupId(groupId);
		var id = CreateId();
		_captures.Add(new(id, payload, runAt) { GroupId = groupId });
		return ValueTask.FromResult(new JobHandle(id));
	}

	private static string? NormalizeGroupId(string? groupId)
	{
		if (string.IsNullOrWhiteSpace(groupId))
			return null;
		if (groupId.Length > 128)
			throw new ArgumentException("A fair queue group id cannot exceed 128 characters.", nameof(groupId));
		return groupId;
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

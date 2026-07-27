namespace Immediate.Jobs.Testing;

/// <summary>
/// A storage-free implementation of <see cref="IJobScheduler{TPayload}"/> that records scheduled calls.
/// </summary>
public class CaptureOnlyJobScheduler<TPayload>(TimeProvider? timeProvider = null) : IJobScheduler<TPayload>
{
	private readonly List<ScheduledJobCapture<TPayload>> _captures = [];
	private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

	/// <summary>All calls captured in call order.</summary>
	public IReadOnlyList<ScheduledJobCapture<TPayload>> Captures => _captures;

	/// <summary>The latest captured call, or <see langword="null"/> when none exists.</summary>
	public ScheduledJobCapture<TPayload>? Last => _captures.Count == 0 ? null : _captures[^1];

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> EnqueueAsync(TPayload payload, CancellationToken cancellationToken = default) =>
		CaptureAsync(payload, _timeProvider.GetUtcNow(), groupId: null, cancellationToken);

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> EnqueueAsync(
		TPayload payload,
		CancellationToken cancellationToken,
		string? groupId
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
		CancellationToken cancellationToken,
		string? groupId
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
	) => CaptureAsync(payload, runAt, groupId: null, cancellationToken);

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> ScheduleAtAsync(
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken,
		string? groupId
	) => CaptureAsync(payload, runAt, groupId, cancellationToken);

	/// <summary>Clears every captured call.</summary>
	public void Clear() => _captures.Clear();

	/// <summary>Creates invocation identifiers. Override when a test requires predictable identifiers.</summary>
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
public sealed record ScheduledJobCapture<TPayload>(string Id, TPayload Payload, DateTimeOffset RunAt)
{
	/// <summary>The normalized fair queue group id supplied to the scheduler.</summary>
	public string? GroupId { get; init; }
}

namespace Immediate.Jobs.Testing;

/// <summary>
/// A storage-free scheduler double. Derive from this class and implement a generated job's
/// <c>IScheduler</c> interface to inject a strongly typed capture double.
/// </summary>
public class CaptureOnlyJobScheduler<TPayload>(TimeProvider? timeProvider = null) : IJobScheduler<TPayload>
	where TPayload : IJobRequest
{
	private readonly List<ScheduledJobCapture<TPayload>> _captures = [];
	private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

	/// <summary>All calls captured in call order.</summary>
	public IReadOnlyList<ScheduledJobCapture<TPayload>> Captures => _captures;

	/// <summary>The latest captured call, or <see langword="null"/> when none exists.</summary>
	public ScheduledJobCapture<TPayload>? Last => _captures.Count == 0 ? null : _captures[^1];

	/// <inheritdoc />
	public virtual ValueTask<Guid> Enqueue(TPayload payload, CancellationToken cancellationToken = default) =>
		Capture(payload, _timeProvider.GetUtcNow(), cancellationToken);

	/// <inheritdoc />
	public virtual ValueTask<Guid> Schedule(
		TPayload payload,
		TimeSpan delay,
		CancellationToken cancellationToken = default
	)
	{
		if (delay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(delay), "A job delay cannot be negative.");
		return Capture(payload, _timeProvider.GetUtcNow() + delay, cancellationToken);
	}

	/// <inheritdoc />
	public virtual ValueTask<Guid> ScheduleAt(
		TPayload payload,
		DateTimeOffset runAt,
		CancellationToken cancellationToken = default
	) => Capture(payload, runAt, cancellationToken);

	/// <summary>Clears every captured call.</summary>
	public void Clear() => _captures.Clear();

	/// <summary>Creates invocation identifiers. Override when a test requires predictable identifiers.</summary>
	protected virtual Guid CreateId() => Guid.NewGuid();

	private ValueTask<Guid> Capture(TPayload payload, DateTimeOffset runAt, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var id = CreateId();
		_captures.Add(new(id, payload, runAt));
		return ValueTask.FromResult(id);
	}
}

/// <summary>A captured typed scheduler call.</summary>
public sealed record ScheduledJobCapture<TPayload>(Guid Id, TPayload Payload, DateTimeOffset RunAt)
	where TPayload : IJobRequest;

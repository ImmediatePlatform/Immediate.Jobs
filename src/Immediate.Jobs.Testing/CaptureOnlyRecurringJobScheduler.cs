namespace Immediate.Jobs.Testing;

/// <summary>
/// A storage-free recurring scheduler double. Derive from it and implement a generated recurring job's
/// <c>IScheduler</c> interface when a test needs that exact service type.
/// </summary>
public class CaptureOnlyRecurringJobScheduler : IRecurringJobScheduler
{
	private readonly List<RecurringJobCapture> captures = [];

	/// <summary>All calls captured in call order.</summary>
	public IReadOnlyList<RecurringJobCapture> Captures => captures;

	/// <summary>The latest captured call, or <see langword="null"/> when none exists.</summary>
	public RecurringJobCapture? Last => captures.Count == 0 ? null : captures[^1];

	/// <inheritdoc />
	public virtual ValueTask AddOrUpdateRecurring(
		string name,
		string cron,
		string timeZone = "UTC",
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		captures.Add(new(RecurringJobOperation.AddOrUpdate, name, cron, timeZone));
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public virtual ValueTask RemoveRecurring(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		captures.Add(new(RecurringJobOperation.Remove, name));
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public virtual ValueTask<Guid> TriggerNow(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var id = CreateId();
		captures.Add(new(RecurringJobOperation.Trigger, JobId: id));
		return ValueTask.FromResult(id);
	}

	/// <summary>Clears every captured call.</summary>
	public void Clear() => captures.Clear();

	/// <summary>Creates trigger identifiers. Override when a test requires predictable identifiers.</summary>
	protected virtual Guid CreateId() => Guid.NewGuid();
}

/// <summary>The operation represented by a recurring scheduler capture.</summary>
public enum RecurringJobOperation
{
	/// <summary>Add or replace a durable schedule.</summary>
	AddOrUpdate,
	/// <summary>Remove a durable schedule.</summary>
	Remove,
	/// <summary>Trigger the recurring job immediately.</summary>
	Trigger,
}

/// <summary>A captured recurring scheduler call.</summary>
public sealed record RecurringJobCapture(
	RecurringJobOperation Operation,
	string? Name = null,
	string? Cron = null,
	string? TimeZone = null,
	Guid? JobId = null
);

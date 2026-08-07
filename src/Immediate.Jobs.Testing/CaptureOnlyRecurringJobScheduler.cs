using Immediate.Jobs.Shared.Interfaces;

namespace Immediate.Jobs.Testing;

/// <summary>
/// A storage-free implementation of <see cref="IRecurringJobScheduler"/> that records recurring calls.
/// </summary>
public class CaptureOnlyRecurringJobScheduler : IRecurringJobScheduler
{
	private readonly List<RecurringJobCapture> _captures = [];

	/// <summary>All calls captured in call order.</summary>
	/// <value>The captured recurring scheduler calls.</value>
	public IReadOnlyList<RecurringJobCapture> Captures => _captures;

	/// <summary>The latest captured call, or <see langword="null"/> when none exists.</summary>
	/// <value>The latest captured call, or <see langword="null"/>.</value>
	public RecurringJobCapture? Last => _captures.Count == 0 ? null : _captures[^1];

	/// <inheritdoc />
	public virtual ValueTask AddOrUpdateRecurringAsync(
		string name,
		string cron,
		string timeZone = "UTC",
		CancellationToken cancellationToken = default
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_captures.Add(new(RecurringJobOperation.AddOrUpdate, name, cron, timeZone));
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public virtual ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_captures.Add(new(RecurringJobOperation.Remove, name));
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public virtual ValueTask<JobHandle> TriggerNowAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var id = CreateId();
		_captures.Add(new(RecurringJobOperation.Trigger, JobId: id));
		return ValueTask.FromResult(new JobHandle(id));
	}

	/// <summary>Clears every captured call.</summary>
	public void Clear() => _captures.Clear();

	/// <summary>Creates trigger identifiers. Override when a test requires predictable identifiers.</summary>
	/// <returns>A new trigger identifier.</returns>
	protected virtual string CreateId() => Guid.NewGuid().ToString("N");
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
/// <param name="Operation">The recurring scheduler operation.</param>
/// <param name="Name">The optional schedule name.</param>
/// <param name="Cron">The optional cron expression.</param>
/// <param name="TimeZone">The optional schedule time zone.</param>
/// <param name="JobId">The optional triggered invocation identifier.</param>
public sealed record RecurringJobCapture(
	RecurringJobOperation Operation,
	string? Name = null,
	string? Cron = null,
	string? TimeZone = null,
	string? JobId = null
);

using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Internals;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	A storage-neutral durable job record.
/// </summary>
public sealed record JobRecord
{
	/// <summary>
	/// 	The stable persisted queue name.
	/// </summary>
	public string QueueName { get; init; } = JobQueueDefinition.DefaultName;

	/// <summary>
	/// 	The unique opaque invocation identifier.
	/// </summary>
	public required JobHandle JobHandle { get; init; }

	/// <summary>
	/// 	The generated stable job name.
	/// </summary>
	public required string JobName { get; init; }

	/// <summary>
	/// 	The serialized payload.
	/// </summary>
	[StringSyntax("json")]
	public required string Payload { get; init; }

	/// <summary>
	/// 	Serialized ambient-context envelope captured while enqueueing.
	/// </summary>
	[StringSyntax("json")]
	public string? Context { get; init; }

	/// <summary>
	/// 	The queue-scoped fairness group key, or <see langword="null"/> for an independent tenant.
	/// </summary>
	public string? GroupId { get; init; }

	/// <summary>
	/// 	The current job state.
	/// </summary>
	public required JobState State { get; init; }

	/// <summary>
	/// 	Timestamp at which the job may be acquired.
	/// </summary>
	public required DateTimeOffset DueAt { get; init; }

	/// <summary>
	/// 	Timestamp at which the invocation was created.
	/// </summary>
	public required DateTimeOffset CreatedAt { get; init; }

	/// <summary>
	/// 	Job Execution Number
	/// </summary>
	public int Attempt { get; init; }

	/// <summary>
	/// 	The owning worker identifier, or <see langword="null"/> when the job is not leased.
	/// </summary>
	public string? WorkerId { get; init; }

	/// <summary>
	/// 	The UTC lease-expiry time, or <see langword="null"/> when the job is not leased.
	/// </summary>
	public DateTimeOffset? LeaseExpiresAt { get; init; }

	/// <summary>
	/// 	The latest failure text.
	/// </summary>
	public string? LastError { get; init; }

	/// <summary>
	/// 	Timestamp at which the job was completed.
	/// </summary>
	public DateTimeOffset? CompletedAt { get; init; }

	/// <summary>
	/// 	The unique recurring materialization key, if this is a recurring occurrence.
	/// </summary>
	public string? RecurringKey { get; init; }

	/// <summary>
	/// 	W3C trace parent captured while enqueueing.
	/// </summary>
	public string? TraceParent { get; init; }

	/// <summary>
	/// 	W3C trace state captured while enqueueing.
	/// </summary>
	public string? TraceState { get; init; }

	/// <summary>
	/// 	Trace identifier created for the latest execution attempt.
	/// </summary>
	public string? ExecutionTraceId { get; init; }

	/// <summary>
	/// 	Span identifier created for the latest execution attempt.
	/// </summary>
	public string? ExecutionSpanId { get; init; }

	/// <summary>
	/// 	Timestamp of the latest execution attempt.
	/// </summary>
	public DateTimeOffset? ExecutionStartedAt { get; init; }

	/// <summary>
	/// 	The identifier of the batch containing this invocation, if any.
	/// </summary>
	public BatchHandle? BatchHandle { get; init; }

	/// <summary>
	/// 	Number of incoming continuation dependencies not yet satisfied.
	/// </summary>
	public int RemainingDependencies { get; init; }

	/// <summary>
	/// 	Number of settled incoming continuation dependencies whose parent failed.
	/// </summary>
	public int FailedDependencies { get; init; }
}

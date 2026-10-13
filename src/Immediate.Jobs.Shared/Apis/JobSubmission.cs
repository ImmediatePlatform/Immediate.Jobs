using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared.Apis;

/// <summary>
/// 	The data needed to enqueue or schedule a job.
/// </summary>
public sealed record JobSubmission
{
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
	/// 	The identifier of the batch containing this invocation, if any.
	/// </summary>
	public BatchHandle? BatchHandle { get; init; }

	/// <summary>
	/// 	Number of incoming continuation dependencies not yet satisfied.
	/// </summary>
	public int RemainingDependencies { get; init; }
}

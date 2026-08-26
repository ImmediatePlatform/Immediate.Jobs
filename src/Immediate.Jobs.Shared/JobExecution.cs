using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Untyped execution metadata passed into a generated invoker.
/// </summary>
public sealed record JobExecution
{
	/// <summary>
	/// 	The durable invocation record.
	/// </summary>
	public required JobRecord Record { get; init; }

	/// <summary>
	/// 	The generated job definition.
	/// </summary>
	public required JobDefinition Definition { get; init; }

	/// <summary>
	/// 	The token that is cancelled when execution should stop.
	/// </summary>
	public required CancellationToken CancellationToken { get; init; }

	/// <summary>
	/// 	The continuation buffer for the current attempt, if enabled.
	/// </summary>
	public JobExecutionBuffer? Buffer { get; init; }
}

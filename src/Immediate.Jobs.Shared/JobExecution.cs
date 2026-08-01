using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Untyped execution metadata passed into a generated invoker.
/// </summary>
/// <param name="Record">
/// 	The durable invocation record.
/// </param>
/// <param name="Definition">
/// 	The generated job definition.
/// </param>
/// <param name="CancellationToken">
/// 	The token that is cancelled when execution should stop.
/// </param>
/// <param name="Buffer">
/// 	The continuation buffer for the current attempt, if enabled.
/// </param>
public sealed record JobExecution(
	JobRecord Record,
	JobDefinition Definition,
	CancellationToken CancellationToken,
	JobExecutionBuffer? Buffer = null
);

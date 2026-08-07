namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Immutable compile-time queue settings.
/// </summary>
public sealed record JobQueueDefinition
{
	/// <summary>
	/// 	The built-in queue used by jobs without <c>UsesQueue</c>.
	/// </summary>
	public const string DefaultName = "default";

	/// <summary>
	/// 	The built-in default queue definition.
	/// </summary>
	public static JobQueueDefinition Default { get; } = new() { Name = DefaultName };

	/// <summary>
	/// 	The stable persisted queue name.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// 	The dispatch priority. Larger values are dispatched first.
	/// </summary>
	public int Priority { get; init; }

	/// <summary>
	/// 	Maximum in-flight jobs on one scheduler node. Zero means unbounded.
	/// </summary>
	public int Concurrency { get; init; }
}

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Immutable fair queue settings applied by storage during one acquisition request.
/// </summary>
public sealed record FairQueuePolicy
{
	/// <summary>
	/// 	The in-flight share threshold used to identify noisy groups.
	/// </summary>
	public required double ConcurrencyShareThreshold { get; init; }

	/// <summary>
	/// 	The minimum in-flight job count required for noisy-group detection.
	/// </summary>
	public required int MinInflightForNoisy { get; init; }

	/// <summary>
	/// 	Whether due work is interleaved across groups.
	/// </summary>
	public required bool GroupRoundRobin { get; init; }
}

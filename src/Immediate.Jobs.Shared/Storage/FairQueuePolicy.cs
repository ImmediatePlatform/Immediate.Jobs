namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Immutable fair queue settings applied by storage during one acquisition request.
/// </summary>
/// <param name="ConcurrencyShareThreshold">
/// 	The in-flight share threshold used to identify noisy groups.
/// </param>
/// <param name="MinInflightForNoisy">
/// 	The minimum in-flight job count required for noisy-group detection.
/// </param>
/// <param name="GroupRoundRobin">
/// 	Whether due work is interleaved across groups.
/// </param>
public sealed record FairQueuePolicy(
	double ConcurrencyShareThreshold,
	int MinInflightForNoisy,
	bool GroupRoundRobin
);

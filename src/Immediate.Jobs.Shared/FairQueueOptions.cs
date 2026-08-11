using Immediate.Jobs.Shared.Storage;
using Immediate.Validations.Shared;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Configures noisy-neighbor detection and group scheduling for fair queues.
/// </summary>
[Validate]
public sealed partial class FairQueueOptions : IValidationTarget<FairQueueOptions>
{
	/// <summary>
	///		Indicates whether Fair Queues are configured for use in the current configuration.
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>
	///		In-flight share above which a group may be considered noisy. The value must be greater
	///		than zero and less than or equal to one.
	/// </summary>
	[GreaterThan(0d), LessThanOrEqual(1d)]
	public double ConcurrencyShareThreshold { get; set; } = 0.10;

	/// <summary>
	/// 	Minimum number of a group's in-flight jobs before it may be considered noisy.
	/// </summary>
	[GreaterThan(0)]
	public int MinInflightForNoisy { get; set; } = 30;

	/// <summary>
	/// 	Whether due work is interleaved across groups independently of noisy-neighbor detection.
	/// </summary>
	/// <value>
	///		<see langword="true"/> to interleave due work across groups; otherwise, <see langword="false"/>.
	/// </value>
	public bool GroupRoundRobin { get; set; } = true;

	internal FairQueuePolicy? ToPolicy() =>
		Enabled
			? new FairQueuePolicy()
			{
				ConcurrencyShareThreshold = ConcurrencyShareThreshold,
				MinInflightForNoisy = MinInflightForNoisy,
				GroupRoundRobin = GroupRoundRobin,
			}
			: null;
}

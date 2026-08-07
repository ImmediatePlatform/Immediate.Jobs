namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An opaque reference to a committed atomic batch.
/// </summary>
public sealed record BatchHandle : ContinuationHandle
{
	/// <summary>
	/// 	The batch identifier.
	/// </summary>
	public required string BatchId
	{
		get; init { ArgumentException.ThrowIfNullOrWhiteSpace(value); field = value; }
	}
}

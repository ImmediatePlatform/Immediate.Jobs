namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An opaque reference to a durable job invocation.
/// </summary>
public sealed record JobHandle : ContinuationHandle
{
	/// <summary>
	/// 	The job identifier.
	/// </summary>
	public required string JobId
	{
		get; init { ArgumentException.ThrowIfNullOrWhiteSpace(value); field = value; }
	}
}

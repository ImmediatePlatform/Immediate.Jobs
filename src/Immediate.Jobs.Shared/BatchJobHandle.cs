namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An opaque reference to a Job which is part of a Batch which is currently being built.
/// </summary>
public sealed class BatchJobHandle
{
	/// <summary>
	/// 	The reference to an in-progress atomic batch buffer.
	/// </summary>
	public required Batch Batch { get; init { ArgumentNullException.ThrowIfNull(value); field = value; } }

	/// <summary>
	/// 	The reference to a durable job invocation.
	/// </summary>
	public required JobHandle JobId { get; init { ArgumentNullException.ThrowIfNull(value); field = value; } }
}

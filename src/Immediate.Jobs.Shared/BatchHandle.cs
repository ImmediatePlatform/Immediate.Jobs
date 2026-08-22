using System.Diagnostics.CodeAnalysis;

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

	/// <summary>
	///		Converts a string batch identifier to it's opaque reference
	/// </summary>
	/// <param name="value">
	///		A batch identifier
	/// </param>
	/// <returns>
	///		An opaque reference containing the provided batch identifier.
	/// </returns>
	[return: NotNullIfNotNull(nameof(value))]
	public static BatchHandle? FromString(string? value) =>
		value switch
		{
			{ } => new() { BatchId = value },
			_ => null,
		};
}

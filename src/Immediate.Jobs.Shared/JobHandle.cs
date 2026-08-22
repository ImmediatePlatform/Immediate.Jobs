using System.Diagnostics.CodeAnalysis;

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

	/// <summary>
	///		Converts a string job identifier to it's opaque reference
	/// </summary>
	/// <param name="value">
	///		A job identifier
	/// </param>
	/// <returns>
	///		An opaque reference containing the provided job identifier.
	/// </returns>
	[return: NotNullIfNotNull(nameof(value))]
	public static JobHandle? FromString(string? value) =>
		value switch
		{
			{ } => new() { JobId = value },
			_ => null,
		};
}

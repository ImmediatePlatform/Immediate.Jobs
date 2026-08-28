using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An opaque reference to a durable job invocation.
/// </summary>
[JsonConverter(typeof(JobHandleConverter))]
public sealed record JobHandle : ContinuationHandle
{
	/// <summary>
	/// 	The job identifier.
	/// </summary>
	public required string Value
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
			{ } => new() { Value = value },
			_ => null,
		};
}

/// <summary>
///		Converter type used to serialize/deserialize <see cref="JobHandle"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class JobHandleConverter : JsonConverter<JobHandle>
{
	/// <inheritdoc />
	public override JobHandle? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType is not JsonTokenType.String)
			throw new InvalidOperationException($"Invalid token type for parsing a `BatchHandle`; type: {reader.TokenType}");

		return reader.GetString() switch
		{
			{ } str => JobHandle.FromString(str),
			_ => null,
		};
	}

	/// <inheritdoc />
	public override void Write(Utf8JsonWriter writer, JobHandle value, JsonSerializerOptions options)
	{
		ArgumentNullException.ThrowIfNull(writer);
		ArgumentNullException.ThrowIfNull(value);

		writer.WriteStringValue(value.Value);
	}
}

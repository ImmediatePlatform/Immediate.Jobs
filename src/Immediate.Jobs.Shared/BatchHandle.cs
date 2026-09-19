using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An opaque reference to a durable batch invocation.
/// </summary>
[JsonConverter(typeof(BatchHandleConverter))]
public sealed record BatchHandle : ContinuationHandle
{
	/// <summary>
	/// 	The batch identifier.
	/// </summary>
	public required string Value
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
			{ } => new() { Value = value },
			_ => null,
		};
}

/// <summary>
///		Converter type used to serialize/deserialize <see cref="BatchHandle"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BatchHandleConverter : JsonConverter<BatchHandle>
{
	/// <inheritdoc />
	public override BatchHandle? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType is not JsonTokenType.String)
			throw new InvalidOperationException($"Invalid token type for parsing a `BatchHandle`; type: {reader.TokenType}");

		return reader.GetString() switch
		{
			{ } str => BatchHandle.FromString(str),
			_ => null,
		};
	}

	/// <inheritdoc />
	public override void Write(Utf8JsonWriter writer, BatchHandle value, JsonSerializerOptions options)
	{
		ArgumentNullException.ThrowIfNull(writer);
		ArgumentNullException.ThrowIfNull(value);

		writer.WriteStringValue(value.Value);
	}
}

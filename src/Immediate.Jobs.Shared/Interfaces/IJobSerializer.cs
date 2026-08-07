using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Immediate.Jobs.Shared.Interfaces;

/// <summary>
/// 	Pluggable payload serialization. The default implementation uses System.Text.Json.
/// </summary>
public interface IJobSerializer
{
	/// <summary>
	/// 	Serializes a typed payload.
	/// </summary>
	/// <typeparam name="TPayload">
	/// 	The payload type.
	/// </typeparam>
	/// <param name="payload">
	/// 	The payload to serialize.
	/// </param>
	/// <returns>
	/// 	The serialized payload.
	/// </returns>
	[RequiresDynamicCode("Use the generated JsonTypeInfo factory overload for Native AOT.")]
	[RequiresUnreferencedCode("Use the generated JsonTypeInfo factory overload when trimming.")]
	string Serialize<TPayload>(TPayload payload);

	/// <summary>
	/// 	Deserializes a typed payload.
	/// </summary>
	/// <typeparam name="TPayload">
	/// 	The payload type.
	/// </typeparam>
	/// <param name="payload">
	/// 	The serialized payload.
	/// </param>
	/// <returns>
	/// 	The deserialized payload.
	/// </returns>
	[RequiresDynamicCode("Use the generated JsonTypeInfo factory overload for Native AOT.")]
	[RequiresUnreferencedCode("Use the generated JsonTypeInfo factory overload when trimming.")]
	TPayload Deserialize<TPayload>(string payload);

	/// <summary>
	/// 	Serializes a payload with generated, AOT-safe JSON metadata.
	/// </summary>
	/// <typeparam name="TPayload">
	/// 	The payload type.
	/// </typeparam>
	/// <param name="payload">
	/// 	The payload to serialize.
	/// </param>
	/// <param name="payloadTypeInfoFactory">
	/// 	The factory that creates generated JSON metadata for the payload type.
	/// </param>
	/// <returns>
	/// 	The serialized payload.
	/// </returns>
	string Serialize<TPayload>(
		TPayload payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	);

	/// <summary>
	/// 	Deserializes a payload with generated, AOT-safe JSON metadata.
	/// </summary>
	/// <typeparam name="TPayload">
	/// 	The payload type.
	/// </typeparam>
	/// <param name="payload">
	/// 	The serialized payload.
	/// </param>
	/// <param name="payloadTypeInfoFactory">
	/// 	The factory that creates generated JSON metadata for the payload type.
	/// </param>
	/// <returns>
	/// 	The deserialized payload.
	/// </returns>
	TPayload Deserialize<TPayload>(
		string payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	);
}

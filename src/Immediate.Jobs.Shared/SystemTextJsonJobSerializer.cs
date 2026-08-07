using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Immediate.Jobs.Shared.Interfaces;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	The default System.Text.Json payload serializer.
/// </summary>
/// <param name="options">
/// 	The JSON serializer options shared by generated schedulers and invokers.
/// </param>
public sealed class SystemTextJsonJobSerializer(JsonSerializerOptions options) : IJobSerializer
{
	private readonly ConcurrentDictionary<Type, JsonTypeInfo> _payloadTypeInfos = new();

	/// <summary>
	/// 	Creates a serializer using web defaults.
	/// </summary>
	public SystemTextJsonJobSerializer()
		: this(new(JsonSerializerDefaults.Web))
	{
	}

	/// <summary>
	/// 	The options shared by generated schedulers and invokers.
	/// </summary>
	public JsonSerializerOptions Options { get; } = options;

	/// <inheritdoc />
	[RequiresDynamicCode("Use the generated JsonTypeInfo factory overload for Native AOT.")]
	[RequiresUnreferencedCode("Use the generated JsonTypeInfo factory overload when trimming.")]
	public string Serialize<TPayload>(TPayload payload) => JsonSerializer.Serialize(payload, Options);

	/// <inheritdoc />
	[RequiresDynamicCode("Use the generated JsonTypeInfo factory overload for Native AOT.")]
	[RequiresUnreferencedCode("Use the generated JsonTypeInfo factory overload when trimming.")]
	public TPayload Deserialize<TPayload>(string payload) =>
		JsonSerializer.Deserialize<TPayload>(payload, Options)
		?? throw new JsonException($"The payload for {typeof(TPayload).FullName} was null.");

	/// <inheritdoc />
	public string Serialize<TPayload>(
		TPayload payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	)
	{
		ArgumentNullException.ThrowIfNull(payloadTypeInfoFactory);
		return JsonSerializer.Serialize(payload, GetPayloadTypeInfo(payloadTypeInfoFactory));
	}

	/// <inheritdoc />
	public TPayload Deserialize<TPayload>(
		string payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	)
	{
		ArgumentNullException.ThrowIfNull(payloadTypeInfoFactory);
		return JsonSerializer.Deserialize(payload, GetPayloadTypeInfo(payloadTypeInfoFactory))
			?? throw new JsonException($"The payload for {typeof(TPayload).FullName} was null.");
	}

	private JsonTypeInfo<TPayload> GetPayloadTypeInfo<TPayload>(
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	) =>
		(JsonTypeInfo<TPayload>)_payloadTypeInfos.GetOrAdd(
			typeof(TPayload),
			static (_, state) => state.Factory(new(state.Options)),
			(Factory: payloadTypeInfoFactory, Options)
		);
}

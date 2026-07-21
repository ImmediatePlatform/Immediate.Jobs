using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared;

/// <summary>The generated invocation boundary used by the worker.</summary>
public interface IJobInvoker
{
	/// <summary>Invokes the job directly from a scoped service provider.</summary>
	ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution);
}

/// <summary>Untyped execution metadata passed into a generated invoker.</summary>
public sealed record JobExecution(
	JobRecord Record,
	JobDefinition Definition,
	CancellationToken CancellationToken
);

/// <summary>Pluggable payload serialization. The default implementation uses System.Text.Json.</summary>
public interface IJobSerializer
{
	/// <summary>Serializes a typed payload.</summary>
	[RequiresDynamicCode("Use the generated JsonTypeInfo factory overload for Native AOT.")]
	[RequiresUnreferencedCode("Use the generated JsonTypeInfo factory overload when trimming.")]
	string Serialize<TPayload>(TPayload payload);

	/// <summary>Deserializes a typed payload.</summary>
	[RequiresDynamicCode("Use the generated JsonTypeInfo factory overload for Native AOT.")]
	[RequiresUnreferencedCode("Use the generated JsonTypeInfo factory overload when trimming.")]
	TPayload Deserialize<TPayload>(string payload);

	/// <summary>Serializes a payload with generated, AOT-safe JSON metadata.</summary>
	string Serialize<TPayload>(
		TPayload payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	);

	/// <summary>Deserializes a payload with generated, AOT-safe JSON metadata.</summary>
	TPayload Deserialize<TPayload>(
		string payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	);
}

/// <summary>The default System.Text.Json payload serializer.</summary>
public sealed class SystemTextJsonJobSerializer(JsonSerializerOptions options) : IJobSerializer
{
	/// <summary>Creates a serializer using web defaults.</summary>
	public SystemTextJsonJobSerializer()
		: this(new(JsonSerializerDefaults.Web))
	{
	}

	/// <summary>The options shared by generated schedulers and invokers.</summary>
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
	) => JsonSerializer.Serialize(payload, payloadTypeInfoFactory(new(Options)));

	/// <inheritdoc />
	public TPayload Deserialize<TPayload>(
		string payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	) => JsonSerializer.Deserialize(payload, payloadTypeInfoFactory(new(Options)))
		?? throw new JsonException($"The payload for {typeof(TPayload).FullName} was null.");
}

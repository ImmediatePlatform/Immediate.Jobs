using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared;

/// <summary>The generated invocation boundary used by the worker.</summary>
public interface IJobInvoker
{
	/// <summary>Invokes the job directly from a scoped service provider.</summary>
	/// <param name="scopedServices">The services for the current job execution scope.</param>
	/// <param name="execution">The metadata for the current execution.</param>
	/// <returns>A value task that represents the asynchronous invocation.</returns>
	ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution);
}

/// <summary>Untyped execution metadata passed into a generated invoker.</summary>
/// <param name="Record">The durable invocation record.</param>
/// <param name="Definition">The generated job definition.</param>
/// <param name="CancellationToken">The token that is cancelled when execution should stop.</param>
/// <param name="Buffer">The continuation buffer for the current attempt, if enabled.</param>
public sealed record JobExecution(
	JobRecord Record,
	JobDefinition Definition,
	CancellationToken CancellationToken,
	JobExecutionBuffer? Buffer = null
);

/// <summary>Pluggable payload serialization. The default implementation uses System.Text.Json.</summary>
public interface IJobSerializer
{
	/// <summary>Serializes a typed payload.</summary>
	/// <typeparam name="TPayload">The payload type.</typeparam>
	/// <param name="payload">The payload to serialize.</param>
	/// <returns>The serialized payload.</returns>
	[RequiresDynamicCode("Use the generated JsonTypeInfo factory overload for Native AOT.")]
	[RequiresUnreferencedCode("Use the generated JsonTypeInfo factory overload when trimming.")]
	string Serialize<TPayload>(TPayload payload);

	/// <summary>Deserializes a typed payload.</summary>
	/// <typeparam name="TPayload">The payload type.</typeparam>
	/// <param name="payload">The serialized payload.</param>
	/// <returns>The deserialized payload.</returns>
	[RequiresDynamicCode("Use the generated JsonTypeInfo factory overload for Native AOT.")]
	[RequiresUnreferencedCode("Use the generated JsonTypeInfo factory overload when trimming.")]
	TPayload Deserialize<TPayload>(string payload);

	/// <summary>Serializes a payload with generated, AOT-safe JSON metadata.</summary>
	/// <typeparam name="TPayload">The payload type.</typeparam>
	/// <param name="payload">The payload to serialize.</param>
	/// <param name="payloadTypeInfoFactory">The factory that creates generated JSON metadata for the payload type.</param>
	/// <returns>The serialized payload.</returns>
	string Serialize<TPayload>(
		TPayload payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	);

	/// <summary>Deserializes a payload with generated, AOT-safe JSON metadata.</summary>
	/// <typeparam name="TPayload">The payload type.</typeparam>
	/// <param name="payload">The serialized payload.</param>
	/// <param name="payloadTypeInfoFactory">The factory that creates generated JSON metadata for the payload type.</param>
	/// <returns>The deserialized payload.</returns>
	TPayload Deserialize<TPayload>(
		string payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	);
}

/// <summary>The default System.Text.Json payload serializer.</summary>
/// <param name="options">The JSON serializer options shared by generated schedulers and invokers.</param>
public sealed class SystemTextJsonJobSerializer(JsonSerializerOptions options) : IJobSerializer
{
	/// <summary>Creates a serializer using web defaults.</summary>
	public SystemTextJsonJobSerializer()
		: this(new(JsonSerializerDefaults.Web))
	{
	}

	/// <summary>The options shared by generated schedulers and invokers.</summary>
	/// <value>The JSON serializer options used for job payloads.</value>
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
		return JsonSerializer.Serialize(payload, payloadTypeInfoFactory(new(Options)));
	}

	/// <inheritdoc />
	public TPayload Deserialize<TPayload>(
		string payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	)
	{
		ArgumentNullException.ThrowIfNull(payloadTypeInfoFactory);
		return JsonSerializer.Deserialize(payload, payloadTypeInfoFactory(new(Options)))
			?? throw new JsonException($"The payload for {typeof(TPayload).FullName} was null.");
	}
}

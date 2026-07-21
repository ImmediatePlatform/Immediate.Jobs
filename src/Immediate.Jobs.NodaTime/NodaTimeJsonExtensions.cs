using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using global::NodaTime;
using global::NodaTime.Serialization.SystemTextJson;

namespace Immediate.Jobs.NodaTime;

/// <summary>Configures NodaTime payload serialization for Immediate.Jobs.</summary>
public static class NodaTimeJsonExtensions
{
	/// <summary>Adds the NodaTime converters used by generated job payload serializers.</summary>
	public static JsonSerializerOptions UseNodaTime(
		this JsonSerializerOptions options,
		IDateTimeZoneProvider? timeZoneProvider = null
	)
	{
		ArgumentNullException.ThrowIfNull(options);
		return options.ConfigureForNodaTime(timeZoneProvider ?? DateTimeZoneProviders.Tzdb);
	}

	/// <summary>Replaces the default job serializer with one configured for NodaTime.</summary>
	public static IServiceCollection AddImmediateJobsNodaTime(
		this IServiceCollection services,
		IDateTimeZoneProvider? timeZoneProvider = null
	)
	{
		ArgumentNullException.ThrowIfNull(services);
		services.Replace(ServiceDescriptor.Singleton<IJobSerializer>(
			_ => new NodaTimeJobSerializer(timeZoneProvider ?? DateTimeZoneProviders.Tzdb)
		));
		return services;
	}
}

/// <summary>A System.Text.Json job serializer configured with NodaTime converters.</summary>
public sealed class NodaTimeJobSerializer : IJobSerializer
{
	private readonly SystemTextJsonJobSerializer serializer;

	/// <summary>Creates a serializer using the TZDB time-zone provider and web JSON defaults.</summary>
	public NodaTimeJobSerializer()
		: this(DateTimeZoneProviders.Tzdb)
	{
	}

	/// <summary>Creates a serializer using the supplied time-zone provider and web JSON defaults.</summary>
	public NodaTimeJobSerializer(IDateTimeZoneProvider timeZoneProvider)
		: this(new JsonSerializerOptions(JsonSerializerDefaults.Web), timeZoneProvider)
	{
	}

	/// <summary>Adds NodaTime converters to and uses the supplied options.</summary>
	public NodaTimeJobSerializer(JsonSerializerOptions options, IDateTimeZoneProvider? timeZoneProvider = null)
	{
		ArgumentNullException.ThrowIfNull(options);
		Options = options.UseNodaTime(timeZoneProvider);
		serializer = new(Options);
	}

	/// <summary>The configured serializer options.</summary>
	public JsonSerializerOptions Options { get; }

	/// <inheritdoc />
	public string Serialize<TPayload>(TPayload payload) => serializer.Serialize(payload);

	/// <inheritdoc />
	public TPayload Deserialize<TPayload>(string payload) => serializer.Deserialize<TPayload>(payload);

	/// <inheritdoc />
	public string Serialize<TPayload>(
		TPayload payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	) => serializer.Serialize(payload, payloadTypeInfoFactory);

	/// <inheritdoc />
	public TPayload Deserialize<TPayload>(
		string payload,
		Func<JsonSerializerOptions, JsonTypeInfo<TPayload>> payloadTypeInfoFactory
	) => serializer.Deserialize(payload, payloadTypeInfoFactory);
}

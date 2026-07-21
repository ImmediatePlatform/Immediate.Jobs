using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs;

/// <summary>Captures ambient state while enqueueing and restores it in a job execution scope.</summary>
/// <typeparam name="TContext">The durable, serializable context value.</typeparam>
public interface IJobContextExtractor<TContext>
{
	/// <summary>The stable key used for this context slice in persisted job envelopes.</summary>
	string Key { get; }

	/// <summary>Captures context from the enqueueing scope, or returns no value when none is available.</summary>
	ValueTask<TContext?> CaptureAsync(CancellationToken cancellationToken);

	/// <summary>Restores captured context into services in the job execution scope.</summary>
	ValueTask RestoreAsync(TContext context, CancellationToken cancellationToken);
}

/// <summary>Applies a context extractor to a generated job or reusable job marker attribute.</summary>
/// <typeparam name="TExtractor">The extractor type.</typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesJobContextAttribute<TExtractor> : Attribute;

/// <summary>Marks a generated invoker that consumes persisted context slices itself.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IJobContextAwareInvoker;

/// <summary>Runtime helpers used by generated context capture and restore code.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class JobContextEnvelope
{
	private static readonly Action<ILogger, string, Guid, Exception?> LogOrphanedSlice =
		LoggerMessage.Define<string, Guid>(
			LogLevel.Warning,
			new EventId(1, nameof(LogOrphanedSlices)),
			"Skipping orphaned context slice {ContextKey} for job {JobId}"
		);

	/// <summary>Adds one serialized slice and rejects duplicate runtime keys.</summary>
	public static void AddSlice(IDictionary<string, string> slices, string key, string value)
	{
		ArgumentNullException.ThrowIfNull(slices);
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(value);
		if (!slices.TryAdd(key, value))
			throw new InvalidOperationException($"Multiple job context extractors use the key '{key}'.");
	}

	/// <summary>Creates a JSON envelope, or returns no envelope when every extractor captured nothing.</summary>
	public static string? Create(IReadOnlyDictionary<string, string> slices)
	{
		ArgumentNullException.ThrowIfNull(slices);
		if (slices.Count == 0)
			return null;

		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			foreach (var slice in slices)
			{
				writer.WritePropertyName(slice.Key);
				writer.WriteRawValue(slice.Value);
			}
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	/// <summary>Reads an envelope into raw JSON slices keyed with ordinal comparison.</summary>
	public static Dictionary<string, string> Read(string envelope)
	{
		ArgumentNullException.ThrowIfNull(envelope);
		using var document = JsonDocument.Parse(envelope);
		if (document.RootElement.ValueKind != JsonValueKind.Object)
			throw new JsonException("A job context envelope must be a JSON object.");

		var slices = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var property in document.RootElement.EnumerateObject())
		{
			if (!slices.TryAdd(property.Name, property.Value.GetRawText()))
				throw new JsonException($"The job context envelope contains duplicate key '{property.Name}'.");
		}
		return slices;
	}

	/// <summary>Logs context slices left unmatched by the generated invoker.</summary>
	public static void LogOrphanedSlices(
		IServiceProvider scopedServices,
		JobRecord record,
		IEnumerable<string> keys
	)
	{
		ArgumentNullException.ThrowIfNull(scopedServices);
		ArgumentNullException.ThrowIfNull(record);
		ArgumentNullException.ThrowIfNull(keys);
		if (scopedServices.GetService(typeof(ILoggerFactory)) is not ILoggerFactory loggerFactory)
			return;

		var logger = loggerFactory.CreateLogger("Immediate.Jobs.ContextPropagation");
		foreach (var key in keys)
			LogOrphanedSlice(logger, key, record.Id, null);
	}
}

using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Runtime helpers used by generated context capture and restore code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class JobContextEnvelope
{
	private static readonly Action<ILogger, string, string, Exception?> LogOrphanedSlice =
		LoggerMessage.Define<string, string>(
			LogLevel.Warning,
			new EventId(1, nameof(LogOrphanedSlices)),
			"Skipping orphaned context slice {ContextKey} for job {JobId}"
		);

	/// <summary>
	/// 	Adds one serialized slice and rejects duplicate runtime keys.
	/// </summary>
	/// <param name="slices">
	/// 	The envelope slices to update.
	/// </param>
	/// <param name="key">
	/// 	The stable key for the context slice.
	/// </param>
	/// <param name="value">
	/// 	The serialized context-slice value.
	/// </param>
	public static void AddSlice(IDictionary<string, string> slices, string key, string value)
	{
		ArgumentNullException.ThrowIfNull(slices);
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(value);
		if (!slices.TryAdd(key, value))
			throw new ImmediateJobException($"Multiple job context extractors use the key '{key}'.");
	}

	/// <summary>
	/// 	Creates a JSON envelope, or returns no envelope when every extractor captured nothing.
	/// </summary>
	/// <param name="slices">
	/// 	The serialized context slices to include.
	/// </param>
	/// <returns>
	/// 	The JSON envelope, or <see langword="null"/> when <paramref name="slices"/> is empty.
	/// </returns>
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

	/// <summary>
	/// 	Reads an envelope into raw JSON slices keyed with ordinal comparison.
	/// </summary>
	/// <param name="envelope">
	/// 	The JSON envelope to read.
	/// </param>
	/// <returns>
	/// 	The raw JSON slices keyed by their stable context keys.
	/// </returns>
	[SuppressMessage(
		"Design",
		"MA0016:Prefer using collection abstraction instead of implementation",
		Justification = "Hidden API used for context storage/retrieval; needs Dictionary<,> for `Remove(, out)` method."
	)]
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

	/// <summary>
	/// 	Logs context slices left unmatched by the generated invoker.
	/// </summary>
	/// <param name="scopedServices">
	/// 	The services for the current job execution scope.
	/// </param>
	/// <param name="record">
	/// 	The durable record for the current invocation.
	/// </param>
	/// <param name="keys">
	/// 	The unmatched context-slice keys.
	/// </param>
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

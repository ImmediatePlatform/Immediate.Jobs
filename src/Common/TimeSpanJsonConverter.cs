using System.Text.Json;
using System.Text.Json.Serialization;

namespace Immediate.Jobs;

internal sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
{
	public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
		TimeSpanSerializer.Deserialize(reader.GetString()!);

	public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
		writer.WriteStringValue(TimeSpanSerializer.Serialize(value));
}

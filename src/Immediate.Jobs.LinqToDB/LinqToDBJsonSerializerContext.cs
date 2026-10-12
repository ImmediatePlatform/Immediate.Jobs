using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.LinqToDB;

[JsonSerializable(typeof(JobServerSnapshot))]
[JsonSourceGenerationOptions(Converters = [typeof(TimeSpanJsonConverter)])]
[JsonSerializable(typeof(string[]))]
internal sealed partial class LinqToDBJsonSerializerContext : JsonSerializerContext;

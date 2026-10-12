using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.EntityFrameworkCore;

[JsonSerializable(typeof(JobServerSnapshot))]
[JsonSourceGenerationOptions(Converters = [typeof(TimeSpanJsonConverter)])]
[JsonSerializable(typeof(string[]))]
internal sealed partial class EntityFrameworkCoreJsonSerializerContext : JsonSerializerContext;

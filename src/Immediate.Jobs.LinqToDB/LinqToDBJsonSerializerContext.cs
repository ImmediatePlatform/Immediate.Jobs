using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.LinqToDB;

[JsonSerializable(typeof(JobServerSnapshot))]
[JsonSerializable(typeof(JobDefinitionRecord))]
internal sealed partial class LinqToDBJsonSerializerContext : JsonSerializerContext;

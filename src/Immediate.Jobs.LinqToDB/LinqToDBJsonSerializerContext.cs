using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.LinqToDB;

[JsonSerializable(typeof(JobServerSnapshot))]
internal sealed partial class LinqToDBJsonSerializerContext : JsonSerializerContext;

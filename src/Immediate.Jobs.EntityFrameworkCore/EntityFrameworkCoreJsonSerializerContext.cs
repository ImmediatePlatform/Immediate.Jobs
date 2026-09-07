using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.EntityFrameworkCore;

[JsonSerializable(typeof(JobServerSnapshot))]
internal sealed partial class EntityFrameworkCoreJsonSerializerContext : JsonSerializerContext;

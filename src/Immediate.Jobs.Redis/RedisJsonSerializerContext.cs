using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Redis;

[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(RecurringJobSchedule))]
[JsonSerializable(typeof(JobServerSnapshot))]
[JsonSerializable(typeof(JobDefinitionRecord))]
[JsonSerializable(typeof(RedisDefinitionCatalogChanges))]
internal sealed partial class RedisJsonSerializerContext : JsonSerializerContext;

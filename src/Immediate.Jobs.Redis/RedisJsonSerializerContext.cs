using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;

namespace Immediate.Jobs.Redis;

[JsonSerializable(typeof(JobSubmission))]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(RecurringJobSchedule))]
[JsonSerializable(typeof(JobServerSnapshot))]
[JsonSerializable(typeof(JobDefinition))]
[JsonSerializable(typeof(RedisDefinitionCatalogChanges))]
internal sealed partial class RedisJsonSerializerContext : JsonSerializerContext;

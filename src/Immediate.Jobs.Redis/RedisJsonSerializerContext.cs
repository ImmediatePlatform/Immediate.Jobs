using System.Text.Json.Serialization;

namespace Immediate.Jobs.Redis;

[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(RecurringJobSchedule))]
internal sealed partial class RedisJsonSerializerContext : JsonSerializerContext;

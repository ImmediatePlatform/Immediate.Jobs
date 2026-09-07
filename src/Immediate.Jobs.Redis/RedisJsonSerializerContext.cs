using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Redis;

[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(RecurringJobSchedule))]
[JsonSerializable(typeof(JobServerSnapshot))]
internal sealed partial class RedisJsonSerializerContext : JsonSerializerContext;

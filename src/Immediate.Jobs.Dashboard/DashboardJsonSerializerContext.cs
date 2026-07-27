using System.Text.Json.Serialization;

namespace Immediate.Jobs.Dashboard;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(JobMonitoringSnapshot))]
[JsonSerializable(typeof(DashboardState))]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(JobRecord[]))]
[JsonSerializable(typeof(RecurringJobSchedule[]))]
[JsonSerializable(typeof(JobServerSnapshot[]))]
internal sealed partial class DashboardJsonSerializerContext : JsonSerializerContext;

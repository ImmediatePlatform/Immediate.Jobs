using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Dashboard;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(JobMonitoringSnapshot))]
[JsonSerializable(typeof(DashboardState))]
[JsonSerializable(typeof(DashboardJobPage))]
[JsonSerializable(typeof(DashboardJobExecutionPage))]
[JsonSerializable(typeof(JobExecutionRecord))]
[JsonSerializable(typeof(JobExecutionRecord[]))]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(JobRecord[]))]
[JsonSerializable(typeof(JobTelemetryLink[]))]
[JsonSerializable(typeof(RecurringJobSchedule[]))]
[JsonSerializable(typeof(JobServerSnapshot[]))]
[JsonSerializable(typeof(BatchStatus))]
[JsonSerializable(typeof(BatchStatus[]))]
[JsonSerializable(typeof(BatchMemberStatus[]))]
[JsonSerializable(typeof(BatchGraph))]
[JsonSerializable(typeof(JobStatus))]
internal sealed partial class DashboardJsonSerializerContext : JsonSerializerContext;

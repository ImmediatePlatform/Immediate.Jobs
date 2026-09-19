using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Dashboard;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(JobMonitoringSnapshot))]
[JsonSerializable(typeof(DashboardState))]
[JsonSerializable(typeof(DashboardJobPage))]
[JsonSerializable(typeof(DashboardJobExecutionPage))]
[JsonSerializable(typeof(JobExecutionRecord))]
[JsonSerializable(typeof(IReadOnlyList<JobExecutionRecord>))]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(JobRecord[]))]
[JsonSerializable(typeof(IReadOnlyList<JobTelemetryLink>))]
[JsonSerializable(typeof(IReadOnlyList<RecurringJobSchedule>))]
[JsonSerializable(typeof(IReadOnlyList<JobServerSnapshot>))]
[JsonSerializable(typeof(BatchStatus))]
[JsonSerializable(typeof(IReadOnlyList<BatchStatus>))]
[JsonSerializable(typeof(IReadOnlyList<BatchMemberStatus>))]
[JsonSerializable(typeof(BatchGraph))]
[JsonSerializable(typeof(JobStatus))]
internal sealed partial class DashboardJsonSerializerContext : JsonSerializerContext;

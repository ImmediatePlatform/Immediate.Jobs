namespace Immediate.Jobs.Dashboard;

internal sealed record DashboardState(JobMonitoringSnapshot Snapshot, JobRecord[] Jobs);

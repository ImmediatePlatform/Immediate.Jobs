namespace Immediate.Jobs.Dashboard;

internal sealed record DashboardState(
	JobMonitoringSnapshot Snapshot,
	JobRecord[] Jobs,
	BatchStatus[] Batches
);

internal sealed record DashboardJobPage(
	JobRecord[] Items,
	int Skip,
	int Take,
	bool HasNext
);

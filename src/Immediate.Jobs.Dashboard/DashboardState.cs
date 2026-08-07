using Immediate.Jobs.Shared.Apis;

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

internal sealed record DashboardJobExecutionPage(
	JobExecutionRecord[] Items,
	int Skip,
	int Take,
	bool HasNext
);

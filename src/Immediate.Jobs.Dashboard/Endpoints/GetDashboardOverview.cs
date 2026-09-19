using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("overview")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardOverview
{
	internal sealed record Query;

	internal static JsonHttpResult<JobMonitoringSnapshot> TransformResult(JobMonitoringSnapshot result) =>
		TypedResults.Json(result, DashboardJsonSerializerContext.Default.JobMonitoringSnapshot);

	private static async ValueTask<JobMonitoringSnapshot> HandleAsync(
		Query _,
		JobMonitor monitor,
		CancellationToken cancellationToken
	) => await monitor.GetSnapshotAsync(cancellationToken);
}

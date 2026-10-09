using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("definitions")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardDefinitions
{
	internal sealed record Query;

	internal static JsonHttpResult<JobMonitoringDefinitions> TransformResult(JobMonitoringDefinitions result) =>
		TypedResults.Json(result, DashboardJsonSerializerContext.Default.JobMonitoringDefinitions);

	private static async ValueTask<JobMonitoringDefinitions> HandleAsync(
		Query _,
		JobMonitor monitor,
		CancellationToken cancellationToken
	) => await monitor.GetDefinitionsAsync(cancellationToken);
}

using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

namespace Immediate.Jobs.Dashboard;

[Handler]
[MapGet("servers")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardServers
{
	internal sealed record Query;

	internal static JsonHttpResult<IReadOnlyList<JobServerSnapshot>> TransformResult(IReadOnlyList<JobServerSnapshot> result) =>
		TypedResults.Json(result, DashboardJsonSerializerContext.Default.IReadOnlyListJobServerSnapshot);

	private static async ValueTask<IReadOnlyList<JobServerSnapshot>> HandleAsync(
		Query _,
		JobMonitor monitor,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await monitor.GetSnapshotAsync(cancellationToken);
		return [.. snapshot.Servers];
	}
}

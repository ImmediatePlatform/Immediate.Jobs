using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

namespace Immediate.Jobs.Dashboard;

[Handler]
[MapGet("recurring")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardRecurringJobs
{
	internal sealed record Query;

	internal static JsonHttpResult<IReadOnlyList<RecurringJobSchedule>> TransformResult(IReadOnlyList<RecurringJobSchedule> result) =>
		TypedResults.Json(result, DashboardJsonSerializerContext.Default.IReadOnlyListRecurringJobSchedule);

	private static async ValueTask<IReadOnlyList<RecurringJobSchedule>> HandleAsync(
		Query _,
		JobMonitor monitor,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await monitor.GetSnapshotAsync(cancellationToken);
		return [.. snapshot.Recurring];
	}
}

using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

namespace Immediate.Jobs.Dashboard;

[Handler]
[MapGet("jobs")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJobs
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		public JobState? State { get; init; }
		public string? Queue { get; init; }
		public string? Search { get; init; }

		[GreaterThanOrEqual(0)]
		public int? Skip { get; init; }

		[GreaterThan(0)]
		public int? Take { get; init; }
	}

	internal static JsonHttpResult<DashboardJobPage> TransformResult(DashboardJobPage result) =>
		TypedResults.Json(result, DashboardJsonSerializerContext.Default.DashboardJobPage);

	private static async ValueTask<DashboardJobPage> HandleAsync(
		Query query,
		JobMonitor monitor,
		CancellationToken cancellationToken
	)
	{
		var pageSize = Math.Min(query.Take ?? 50, 200);
		var pageStart = query.Skip ?? 0;
		var jobs = await monitor.QueryJobsAsync(new JobQuery
		{
			State = query.State,
			QueueName = query.Queue,
			Search = query.Search,
			Skip = pageStart,
			Take = pageSize + 1,
		}, cancellationToken);
		return new(
			[.. jobs.Take(pageSize)],
			pageStart,
			pageSize,
			jobs.Count > pageSize
		);
	}
}

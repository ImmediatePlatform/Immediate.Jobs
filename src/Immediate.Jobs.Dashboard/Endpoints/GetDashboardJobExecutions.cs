using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("jobs/{jobHandle}/executions")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJobExecutions
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string JobHandle { get; init; }

		[GreaterThanOrEqual(0)]
		public int? Skip { get; init; }

		[GreaterThan(0)]
		public int? Take { get; init; }
	}

	internal static Results<JsonHttpResult<DashboardJobExecutionPage>, NotFound> TransformResult(
		DashboardJobExecutionPage? result
	) => result is null
		? TypedResults.NotFound()
		: TypedResults.Json(result, DashboardJsonSerializerContext.Default.DashboardJobExecutionPage);

	private static async ValueTask<DashboardJobExecutionPage?> HandleAsync(
		Query query,
		JobMonitor monitor,
		CancellationToken cancellationToken
	)
	{
		var pageStart = query.Skip ?? 0;
		var pageSize = Math.Min(query.Take ?? 50, 200);
		var jobs = await monitor.QueryJobsAsync(
			new() { JobHandle = JobHandle.FromString(query.JobHandle), Take = 1 },
			cancellationToken
		);
		if (jobs.Count == 0)
			return null;

		var executions = await monitor.QueryExecutionsAsync(
			JobHandle.FromString(query.JobHandle),
			new()
			{
				Skip = pageStart,
				Take = pageSize + 1,
			},
			cancellationToken);
		return new(
			[.. executions.Take(pageSize)],
			pageStart,
			pageSize,
			executions.Count > pageSize
		);
	}
}

using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("jobs/{jobHandle}")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJob
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string JobHandle { get; init; }
	}

	internal static Results<JsonHttpResult<JobRecord>, NotFound> TransformResult(JobRecord? result) =>
		result is null
			? TypedResults.NotFound()
			: TypedResults.Json(result, DashboardJsonSerializerContext.Default.JobRecord);

	private static async ValueTask<JobRecord?> HandleAsync(
		Query query,
		JobMonitor monitor,
		CancellationToken cancellationToken
	)
	{
		var jobs = await monitor.QueryJobsAsync(
			new() { JobHandle = JobHandle.FromString(query.JobHandle), Take = 1 },
			cancellationToken
		);
		return jobs.SingleOrDefault();
	}
}

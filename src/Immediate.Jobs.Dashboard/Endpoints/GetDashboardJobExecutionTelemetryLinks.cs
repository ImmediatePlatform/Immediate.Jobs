using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("jobs/{jobId}/executions/{executionNumber:int}/telemetry-links")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJobExecutionTelemetryLinks
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string JobId { get; init; }

		[GreaterThan(0)]
		public required int ExecutionNumber { get; init; }
	}

	internal static Results<JsonHttpResult<IReadOnlyList<JobTelemetryLink>>, NotFound> TransformResult(
		IReadOnlyList<JobTelemetryLink>? result
	) => DashboardApiEndpointOperations.TransformTelemetryLinksResult(result);

	private static ValueTask<IReadOnlyList<JobTelemetryLink>?> HandleAsync(
		Query query,
		JobMonitor monitor,
		IOptions<ImmediateJobsDashboardOptions> options,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.GetJobTelemetryLinksAsync(
		JobHandle.FromString(query.JobId),
		query.ExecutionNumber,
		monitor,
		options.Value,
		cancellationToken
	);
}

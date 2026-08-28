using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("jobs/{jobHandle}/telemetry-links")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardJobTelemetryLinks
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string JobHandle { get; init; }
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
		JobHandle.FromString(query.JobHandle),
		executionNumber: null,
		monitor,
		options.Value,
		cancellationToken
	);
}

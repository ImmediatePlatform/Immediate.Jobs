using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapPost("jobs/{jobHandle}/cancel")]
[MapGroup<DashboardApi>]
internal static partial class CancelDashboardJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string JobHandle { get; init; }
	}

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformMutationResult(result);

	private static ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		JobMonitor monitor,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.MutateJobAsync(
		() => monitor.CancelJobAsync(JobHandle.FromString(command.JobHandle), cancellationToken)
	);
}

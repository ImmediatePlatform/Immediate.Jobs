using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

namespace Immediate.Jobs.Dashboard;

[Handler]
[MapPost("recurring/{name}/pause")]
[MapGroup<DashboardApi>]
internal static partial class PauseDashboardRecurringJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string Name { get; init; }
	}

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformMutationResult(result);

	private static ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		JobMonitor monitor,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.MutateRecurringAsync(
		() => monitor.PauseRecurringAsync(command.Name, cancellationToken)
	);
}

using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

namespace Immediate.Jobs.Dashboard;

[Handler]
[MapPost("recurring/{name}/trigger")]
[MapGroup<DashboardApi>]
internal static partial class TriggerDashboardRecurringJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string Name { get; init; }
	}

	internal static Results<StatusCodeHttpResult, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformTriggerResult(result);

	private static async ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		JobMonitor monitor,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await monitor.TriggerRecurringAsync(command.Name, cancellationToken);
			return DashboardMutationResult.Accepted;
		}
		catch (KeyNotFoundException)
		{
			return DashboardMutationResult.NotFound;
		}
		catch (ImmediateJobException exception)
		{
			return DashboardMutationResult.Conflict(exception.Message);
		}
	}
}

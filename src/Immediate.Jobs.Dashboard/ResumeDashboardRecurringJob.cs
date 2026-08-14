using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Storage;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

namespace Immediate.Jobs.Dashboard;

[Handler]
[MapPost("recurring/{name}/resume")]
[MapGroup<DashboardApi>]
internal static partial class ResumeDashboardRecurringJob
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
		IJobStorage storage,
		CancellationToken cancellationToken
	) => storage is IRecurringJobStorage recurringStorage
		? DashboardApiEndpointOperations.MutateRecurringAsync(
			() => recurringStorage.ResumeRecurringAsync(command.Name, cancellationToken)
		)
		: ValueTask.FromResult(DashboardMutationResult.NotFound);
}

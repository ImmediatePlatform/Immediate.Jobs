using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http.HttpResults;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

namespace Immediate.Jobs.Dashboard;

[Handler]
[MapDelete("batches/{batchId}")]
[MapGroup<DashboardApi>]
internal static partial class DeleteDashboardBatch
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[NotEmpty]
		public required string BatchId { get; init; }
	}

	internal static Results<NoContent, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformMutationResult(result);

	private static ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		JobMonitor monitor,
		CancellationToken cancellationToken
	) => DashboardApiEndpointOperations.MutateBatchAsync(
		() => monitor.DeleteBatchAsync(command.BatchId, cancellationToken)
	);
}

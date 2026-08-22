using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("batches/{batchId}/graph")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardBatchGraph
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string BatchId { get; init; }
	}

	internal static Results<JsonHttpResult<BatchGraph>, NotFound> TransformResult(BatchGraph? result) =>
		result is null
			? TypedResults.NotFound()
			: TypedResults.Json(result, DashboardJsonSerializerContext.Default.BatchGraph);

	private static async ValueTask<BatchGraph?> HandleAsync(
		Query query,
		JobMonitor monitor,
		CancellationToken cancellationToken
	)
	{
		return await monitor.GetBatchGraphAsync(BatchHandle.FromString(query.BatchId), cancellationToken);
	}
}

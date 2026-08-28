using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("batches/{batchHandle}")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardBatch
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string BatchHandle { get; init; }
	}

	internal static Results<JsonHttpResult<BatchStatus>, NotFound> TransformResult(BatchStatus? result) =>
		result is null
			? TypedResults.NotFound()
			: TypedResults.Json(result, DashboardJsonSerializerContext.Default.BatchStatus);

	private static async ValueTask<BatchStatus?> HandleAsync(
		Query query,
		JobMonitor monitor,
		CancellationToken cancellationToken
	)
	{
		return await monitor.GetBatchAsync(BatchHandle.FromString(query.BatchHandle), cancellationToken);
	}
}

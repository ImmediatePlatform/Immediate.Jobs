using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("batches")]
[MapGroup<DashboardApi>]
internal static partial class GetDashboardBatches
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		public BatchState? State { get; init; }

		[GreaterThanOrEqual(0)]
		public int? Skip { get; init; }

		[GreaterThan(0)]
		public int? Take { get; init; }
	}

	internal static Results<JsonHttpResult<IReadOnlyList<BatchStatus>>, NotFound> TransformResult(IReadOnlyList<BatchStatus>? result) =>
		result is null
			? TypedResults.NotFound()
			: TypedResults.Json(result, DashboardJsonSerializerContext.Default.IReadOnlyListBatchStatus);

	private static async ValueTask<IReadOnlyList<BatchStatus>?> HandleAsync(
		Query query,
		JobMonitor monitor,
		CancellationToken cancellationToken
	)
	{
		return await monitor.QueryBatchesAsync(new()
		{
			State = query.State,
			Skip = query.Skip ?? 0,
			Take = Math.Min(query.Take ?? 100, 500),
		}, cancellationToken);
	}
}

using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("batches/{batchId}/stream")]
[MapGroup<DashboardApi>]
internal static partial class StreamDashboardBatch
{
	[Validate]
	internal sealed partial record Query : IValidationTarget<Query>
	{
		[NotEmpty]
		public required string BatchId { get; init; }
	}

	internal static EmptyHttpResult TransformResult() => TypedResults.Empty;

	private static async ValueTask HandleAsync(
		Query query,
		IHttpContextAccessor httpContextAccessor,
		JobMonitor monitor,
		TimeProvider timeProvider,
		IOptions<ImmediateJobsDashboardOptions> options,
		CancellationToken cancellationToken
	)
	{
		await DashboardApiEndpointOperations.StreamBatchEventsAsync(
			httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active dashboard HTTP request was found."),
			query.BatchId,
			monitor,
			timeProvider,
			options.Value.UpdateInterval,
			cancellationToken
		);
	}
}

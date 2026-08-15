using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Dashboard.Endpoints;

[Handler]
[MapGet("events")]
[MapGroup<DashboardApi>]
internal static partial class StreamDashboardEvents
{
	internal sealed record Query;

	internal static EmptyHttpResult TransformResult() => TypedResults.Empty;

	private static async ValueTask HandleAsync(
		Query _,
		IHttpContextAccessor httpContextAccessor,
		JobMonitor monitor,
		TimeProvider timeProvider,
		IOptions<ImmediateJobsDashboardOptions> options,
		CancellationToken cancellationToken
	)
	{
		await DashboardApiEndpointOperations.StreamEventsAsync(
			httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No active dashboard HTTP request was found."),
			monitor,
			timeProvider,
			options.Value.UpdateInterval,
			cancellationToken
		);
	}
}

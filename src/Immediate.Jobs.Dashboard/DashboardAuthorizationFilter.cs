using Microsoft.AspNetCore.Http;

namespace Immediate.Jobs.Dashboard;

internal sealed class DashboardAuthorizationFilter(Func<HttpContext, ValueTask<bool>> authorizer) : IEndpointFilter
{
	public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
		await authorizer(context.HttpContext)
			? await next(context)
			: Results.StatusCode(StatusCodes.Status403Forbidden);
}

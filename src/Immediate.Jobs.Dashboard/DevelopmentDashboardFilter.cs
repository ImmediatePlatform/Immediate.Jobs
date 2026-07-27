using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Immediate.Jobs.Dashboard;

internal sealed class DevelopmentDashboardFilter : IEndpointFilter
{
	public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		var environment = context.HttpContext.RequestServices.GetService<IWebHostEnvironment>();
		return environment?.IsDevelopment() is true
			? next(context)
			: ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
	}
}

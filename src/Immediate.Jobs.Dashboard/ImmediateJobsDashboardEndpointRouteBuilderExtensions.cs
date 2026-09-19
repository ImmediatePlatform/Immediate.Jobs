using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Dashboard;

/// <summary>Maps the embedded Immediate.Jobs dashboard and monitoring API.</summary>
public static class ImmediateJobsDashboardEndpointRouteBuilderExtensions
{
	/// <summary>Maps the dashboard at <c>/jobs</c>.</summary>
	/// <param name="endpoints">The endpoint route builder to add the dashboard to.</param>
	/// <returns>The route group containing the dashboard endpoints.</returns>
	public static RouteGroupBuilder MapImmediateJobsDashboard(
		this IEndpointRouteBuilder endpoints
	) => endpoints.MapImmediateJobsDashboard("/jobs");

	/// <summary>Maps the dashboard and its API under <paramref name="prefix"/>.</summary>
	/// <param name="endpoints">The endpoint route builder to add the dashboard to.</param>
	/// <param name="prefix">The URL path prefix under which to map the dashboard.</param>
	/// <returns>The route group containing the dashboard endpoints.</returns>
	public static RouteGroupBuilder MapImmediateJobsDashboard(
		this IEndpointRouteBuilder endpoints,
		string prefix
	)
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

		if (prefix[0] != '/')
			prefix = "/" + prefix;

		prefix = prefix.TrimEnd('/');
		if (prefix.Length == 0)
			throw new ArgumentException("The dashboard must be mapped below the application root.", nameof(prefix));

		var options = endpoints.ServiceProvider.GetService<IOptions<ImmediateJobsDashboardOptions>>()?.Value
			?? throw new InvalidOperationException(
				"Dashboard services are not registered. Call services.AddImmediateJobsDashboard() before building the application."
			);

		var group = endpoints.MapGroup(prefix).WithTags("Immediate.Jobs Dashboard");
		if (options.AuthorizationPolicy is { } policy)
			_ = group.RequireAuthorization(new AuthorizeAttribute(policy));
		else if (options.RestrictToDevelopmentEnvironment)
			_ = group.AddEndpointFilter(new DevelopmentDashboardFilter());

		_ = group.AddEndpointFilter(new DashboardValidationFilter());

		_ = group.MapImmediateJobsDashboardEndpoints();

		_ = group
			.MapGet(
				"/",
				(Delegate)(
					(HttpContext context) =>
						context.Request.Path.Value is { Length: > 0 } path && path[^1] == '/'
							? DashboardAssets.GetIndexAsync(context, prefix)
							: Task.FromResult(Results.Redirect(prefix + "/"))
				)
			)
			.ExcludeFromDescription();

		_ = group.MapGet("/app.css", () => DashboardAssets.GetAsync("app.css")).ExcludeFromDescription();
		_ = group.MapGet("/app.js", () => DashboardAssets.GetAsync("app.js")).ExcludeFromDescription();

		_ = group.MapGet(
			"/{**path}",
			(string path, HttpContext context) =>
				path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
					? Task.FromResult(Results.NotFound())
					: DashboardAssets.GetIndexAsync(context, prefix)
			)
			.WithOrder(int.MaxValue)
			.ExcludeFromDescription();

		return group;
	}
}

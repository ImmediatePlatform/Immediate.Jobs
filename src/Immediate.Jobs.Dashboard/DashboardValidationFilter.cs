using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;

namespace Immediate.Jobs.Dashboard;

internal sealed class DashboardValidationFilter : IEndpointFilter
{
	public async ValueTask<object?> InvokeAsync(
		EndpointFilterInvocationContext context,
		EndpointFilterDelegate next
	)
	{
		try
		{
			return await next(context).ConfigureAwait(false);
		}
		catch (ValidationException exception)
		{
			var errors = exception.Errors
				.GroupBy(error => error.PropertyName, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(
					group => group.Key,
					group => group.Select(error => error.ErrorMessage).ToArray(),
					StringComparer.OrdinalIgnoreCase
				);
			return TypedResults.ValidationProblem(errors, title: exception.Title);
		}
	}
}

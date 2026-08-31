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
			return await next(context);
		}
		catch (ValidationException exception)
		{
#pragma warning disable RS0030 // `ValidationProblem` demands an array
			var errors = exception.Errors
				.GroupBy(error => error.PropertyName, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(
					group => group.Key,
					group => group.Select(error => error.ErrorMessage).ToArray(),
					StringComparer.OrdinalIgnoreCase
				);
#pragma warning restore RS0030

			return TypedResults.ValidationProblem(errors, title: exception.Title);
		}
	}
}

using Immediate.Validations.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.Dashboard;

/// <summary>
///		Registers the Immediate.Jobs dashboard API handlers and validation pipeline.
/// </summary>
public static class ImmediateJobsDashboardServiceCollectionExtensions
{
	/// <summary>
	///		Adds the services required by <c>MapImmediateJobsDashboard</c>.
	/// </summary>
	/// <param name="builder">
	///		The builder used to configure <c>Immediate.Jobs</c>
	/// </param>
	/// <returns>
	///		The service collection for further configuration.
	/// </returns>
	public static IImmediateJobsDashboardBuilder AddImmediateJobsDashboard(
		this IImmediateJobsBuilder builder
	)
	{
		ArgumentNullException.ThrowIfNull(builder);

		var optionsBuilder = builder.Services
			.AddOptionsWithValidateOnStart<ImmediateJobsDashboardOptions>()
			.Validate(
				o =>
				{
					ValidationException.ThrowIfInvalid(o, $@"Validation error for ""{nameof(ImmediateJobsDashboardOptions)}""");
					return true;
				}
			);

		_ = builder.Services.AddHttpContextAccessor();
		_ = builder.Services.AddImmediateJobsDashboardHandlers();

		return new ImmediateJobsDashboardBuilder(builder, optionsBuilder);
	}
}

using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.Dashboard;

/// <summary>Registers the Immediate.Jobs dashboard API handlers and validation pipeline.</summary>
public static class ImmediateJobsDashboardServiceCollectionExtensions
{
	/// <summary>Adds the services required by <c>MapImmediateJobsDashboard</c>.</summary>
	/// <param name="services">The service collection to add dashboard services to.</param>
	/// <param name="configure">An optional callback that configures the dashboard.</param>
	/// <returns>The service collection for further configuration.</returns>
	public static IServiceCollection AddImmediateJobsDashboard(
		this IServiceCollection services,
		Action<ImmediateJobsDashboardOptions>? configure = null
	)
	{
		ArgumentNullException.ThrowIfNull(services);

		var optionsBuilder = services
			.AddOptionsWithValidateOnStart<ImmediateJobsDashboardOptions>()
			.Validate(
				static options => options.UpdateInterval > TimeSpan.Zero,
				"The dashboard update interval must be positive."
			);
		if (configure is not null)
			optionsBuilder.Configure(configure);

		_ = services.AddHttpContextAccessor();
		_ = services.AddImmediateJobsDashboardHandlers();
		_ = services.AddImmediateJobsCore();
		return services;
	}
}

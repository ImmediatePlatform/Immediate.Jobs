using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Reports job-scheduling service liveness.
/// </summary>
/// <param name="service">
/// 	The job-scheduling service whose liveness is checked.
/// </param>
public sealed class ServiceHealthCheck(JobSchedulingService service) : IHealthCheck
{
	/// <inheritdoc />
	public async Task<HealthCheckResult> CheckHealthAsync(
		HealthCheckContext context,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(context);

		return service.IsHealthy() switch
		{
			true => HealthCheckResult.Healthy("The Immediate.Jobs scheduling service is healthy."),
			false => new HealthCheckResult(
				context.Registration.FailureStatus,
				"The Immediate.Jobs scheduling service is unavailable."
			),
		};
	}
}

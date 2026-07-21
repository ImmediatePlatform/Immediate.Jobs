using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Immediate.Jobs;

/// <summary>Reports scheduler-loop liveness and provider connectivity.</summary>
public sealed class ImmediateJobsHealthCheck(
	IJobStorage storage,
	JobSchedulerState state,
	ImmediateJobsOptions options,
	TimeProvider timeProvider
) : IHealthCheck
{
	/// <inheritdoc />
	public async Task<HealthCheckResult> CheckHealthAsync(
		HealthCheckContext context,
		CancellationToken cancellationToken = default
	)
	{
		if (!await storage.IsHealthyAsync(cancellationToken).ConfigureAwait(false))
			return new(context.Registration.FailureStatus, "The Immediate.Jobs storage provider is unavailable.");

		if (state.StartedAt is null)
			return HealthCheckResult.Degraded("The Immediate.Jobs scheduler has not started.");

		var allowedSilence = TimeSpan.FromTicks(options.PollingInterval.Ticks * 3);
		if (state.LastHeartbeat is not { } heartbeat || timeProvider.GetUtcNow() - heartbeat > allowedSilence)
			return new(context.Registration.FailureStatus, "The Immediate.Jobs scheduler heartbeat is stale.");

		return HealthCheckResult.Healthy("The Immediate.Jobs scheduler and storage provider are healthy.");
	}
}

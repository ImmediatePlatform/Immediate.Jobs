using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Immediate.Jobs.Shared;

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
		ArgumentNullException.ThrowIfNull(context);
		var data = new Dictionary<string, object>
		{
			["storageCapabilities"] = storage.GetCapabilities().ToString(),
		};

		if (!await storage.IsHealthyAsync(cancellationToken).ConfigureAwait(false))
			return new(
				context.Registration.FailureStatus,
				"The Immediate.Jobs storage provider is unavailable.",
				data: data
			);

		if (state.StartedAt is null)
			return HealthCheckResult.Degraded("The Immediate.Jobs scheduler has not started.", data: data);

		var allowedSilence = TimeSpan.FromTicks(options.PollingInterval.Ticks * 3);
		if (state.LastHeartbeat is not { } heartbeat || timeProvider.GetUtcNow() - heartbeat > allowedSilence)
			return new(
				context.Registration.FailureStatus,
				"The Immediate.Jobs scheduler heartbeat is stale.",
				data: data
			);

		return HealthCheckResult.Healthy(
			"The Immediate.Jobs scheduler and storage provider are healthy.",
			data
		);
	}
}

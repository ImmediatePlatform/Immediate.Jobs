using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Reports storage-provider connectivity.
/// </summary>
/// <param name="storage">
/// 	The storage provider whose connectivity is checked.
/// </param>
public sealed class StorageHealthCheck(IJobStorage storage) : IHealthCheck
{
	/// <inheritdoc />
	public async Task<HealthCheckResult> CheckHealthAsync(
		HealthCheckContext context,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(context);
		await TaskScheduler.Yield();

		var data = new Dictionary<string, object>(StringComparer.Ordinal)
		{
			["storageCapabilities"] = storage.GetCapabilities().ToString(),
		};

		return await storage.IsHealthyAsync(cancellationToken) switch
		{
			true => HealthCheckResult.Healthy("The Immediate.Jobs storage provider is healthy.", data),
			false => new(
				context.Registration.FailureStatus,
				"The Immediate.Jobs storage provider is unavailable.",
				data: data
			),
		};
	}
}

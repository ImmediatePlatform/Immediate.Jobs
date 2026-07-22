using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Jobs;

[Handler, Job("aspire-heartbeat", Cron = "0 * * * * *")]
public sealed partial class AspireHeartbeatJob(
	ILogger<AspireHeartbeatJob> logger,
	TimeProvider timeProvider
)
{
	private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		logger.LogInformation(
			"Recurring Aspire heartbeat {JobId} fired at {FiredAt}",
			payload.JobDetails?.JobId,
			timeProvider.GetUtcNow()
		);
		return ValueTask.CompletedTask;
	}
}

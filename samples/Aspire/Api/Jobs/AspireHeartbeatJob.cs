using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Jobs;

[Handler, Job(Name = "aspire-heartbeat", Cron = "0 * * * * *")]
public sealed partial class AspireHeartbeatJob(
	ILogger<AspireHeartbeatJob> logger,
	TimeProvider timeProvider
)
{
	private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		logger.LogInformation(
			"Recurring Aspire heartbeat {JobHandle} fired at {FiredAt}",
			payload.JobDetails?.JobHandle,
			timeProvider.GetUtcNow()
		);
		return ValueTask.CompletedTask;
	}
}

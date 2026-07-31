using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Jobs;

[Handler, Job(
	Name = "third-attempt-success-demo",
	MaxAttempts = 3,
	Backoff = BackoffStrategy.Fixed,
	BackoffBase = "00:05:00"
)]
public sealed partial class ThirdAttemptSuccessJob(ILogger<ThirdAttemptSuccessJob> logger)
{
	public sealed record Payload(Guid RunId) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		var details = payload.JobDetails
			?? throw new InvalidOperationException("Job details were not populated.");
		if (details.Attempt < 3)
		{
			logger.LogWarning(
				"Retry demo {RunId}: intentionally failing attempt {Attempt}; retry is due in five minutes",
				payload.RunId,
				details.Attempt
			);
			throw new InvalidOperationException("Expected retry demo failure before the third attempt.");
		}

		logger.LogInformation(
			"Retry demo {RunId}: succeeded on attempt {Attempt}",
			payload.RunId,
			details.Attempt
		);
		return ValueTask.CompletedTask;
	}
}

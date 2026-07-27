using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Jobs;

[Handler, Job("fair-queue-demo")]
public sealed partial class FairQueueDemoJob(ILogger<FairQueueDemoJob> logger)
{
	public sealed record Payload(Guid RunId, int Sequence, string Kind);

	private async ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		logger.LogInformation(
			"Fair-queue demo {RunId}: starting {Kind} job {Sequence}",
			payload.RunId,
			payload.Kind,
			payload.Sequence
		);
		await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
		logger.LogInformation(
			"Fair-queue demo {RunId}: completed {Kind} job {Sequence}",
			payload.RunId,
			payload.Kind,
			payload.Sequence
		);
	}
}

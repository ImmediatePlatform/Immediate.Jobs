using Immediate.Handlers.Shared;
using Immediate.Jobs.Aspire.Api.Context;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Jobs;

[Handler, Job(Name = "aspire-greeting", MaxAttempts = 3), UsesJobContext<RequestContextExtractor>]
public sealed partial class AspireGreetingJob(
	ILogger<AspireGreetingJob> logger,
	CurrentRequestContext currentRequestContext
)
{
	public sealed record Payload(string Name);

	private async ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		logger.LogInformation(
			"Preparing a greeting for {Name}; request IP {ClientIpAddress}, User-Agent {UserAgent}",
			payload.Name,
			currentRequestContext.Value?.ClientIpAddress,
			currentRequestContext.Value?.UserAgent
		);
		await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
		logger.LogInformation("Hello, {Name}!", payload.Name);
	}
}

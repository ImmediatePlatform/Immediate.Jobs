using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Jobs;

[Handler, Job(Name = "continuation-branch-root", MaxAttempts = 1)]
public sealed partial class ContinuationBranchRootJob(ILogger<ContinuationBranchRootJob> logger)
{
	public sealed record Payload(Guid RunId, bool Fail);

	private async ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		logger.LogInformation(
			"Continuation branch demo {RunId}: running root job; intentional failure is {Fail}",
			payload.RunId,
			payload.Fail
		);
		await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
		if (payload.Fail)
			throw new InvalidOperationException("Expected continuation branch demo failure.");
	}
}

[Handler, Job(Name = "continuation-branch-success")]
public sealed partial class ContinuationBranchSuccessJob(ILogger<ContinuationBranchSuccessJob> logger)
{
	public sealed record Payload(Guid RunId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		logger.LogInformation(
			"Continuation branch demo {RunId}: the success branch ran",
			payload.RunId
		);
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "continuation-branch-failure")]
public sealed partial class ContinuationBranchFailureJob(ILogger<ContinuationBranchFailureJob> logger)
{
	public sealed record Payload(Guid RunId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		logger.LogInformation(
			"Continuation branch demo {RunId}: the failure branch ran",
			payload.RunId
		);
		return ValueTask.CompletedTask;
	}
}

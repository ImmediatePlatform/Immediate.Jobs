using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

namespace SqliteSample.Jobs;

[Handler, Job(Name = "max-active-demo", MaxConcurrency = 2)]
public sealed partial class MaxActiveDemoJob(ILogger<MaxActiveDemoJob> logger)
{
	private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken)
	{
		logger.LogInformation("Starting concurrency demo {JobHandle}", request.JobDetails?.JobHandle);
		await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
		logger.LogInformation("Completed concurrency demo {JobHandle}", request.JobDetails?.JobHandle);
	}
}

[Handler, Job(Name = "fixed-window-demo", FixedWindowMax = 3, FixedWindowPeriod = "00:01:00")]
public sealed partial class FixedWindowDemoJob(ILogger<FixedWindowDemoJob> logger)
{
	private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken)
	{
		logger.LogInformation("Starting concurrency demo {JobHandle}", request.JobDetails?.JobHandle);
		await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
		logger.LogInformation("Completed concurrency demo {JobHandle}", request.JobDetails?.JobHandle);
	}
}

[Handler, Job(Name = "sliding-window-demo", SlidingWindowMax = 3, SlidingWindowPeriod = "00:01:00")]
public sealed partial class SlidingWindowDemoJob(ILogger<SlidingWindowDemoJob> logger)
{
	private async ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken)
	{
		logger.LogInformation("Starting concurrency demo {JobHandle}", request.JobDetails?.JobHandle);
		await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
		logger.LogInformation("Completed concurrency demo {JobHandle}", request.JobDetails?.JobHandle);
	}
}

using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests;

public sealed class JobSchedulingServiceTests
{
	private sealed class FailingTelemetryStorage(TimeProvider timeProvider) : CapturingJobStorage(timeProvider)
	{
		public override async ValueTask SetExecutionTelemetryAsync(
			JobHandle jobHandle,
			int executionNumber,
			string workerId,
			string? traceId,
			string? spanId,
			DateTimeOffset startedAt,
			CancellationToken cancellationToken = default
		)
		{
			throw new InvalidOperationException("Telemetry persistence failure.");
		}
	}

	[Fact]
	public async Task TelemetryPersistenceFailureDoesNotConsumeAnAttempt()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

		var state = new BatchWorkflowState();
		var services = new ServiceCollection();

		services.AddLogging();
		services.AddSingleton(state);
		services.AddSingleton(new DynamicExpansionState());
		services.AddSingleton(new ExecutionBufferProbeState());
		services.AddSingleton<TimeProvider>(timeProvider);
		services.AddImmediateJobsFunctionalTestsHandlers();

		services.AddImmediateJobsFunctionalTestsJobs()
			.ConfigureStorage(o => o.UseStorage<FailingTelemetryStorage>().UseDistributed());

		await using var provider = services.BuildServiceProvider();

		var scheduler = provider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var service = provider.GetRequiredService<JobSchedulingService>();
		var storage = provider.GetRequiredService<FailingTelemetryStorage>();

		var handle = await scheduler.EnqueueAsync(new("telemetry-failure"), cancellationToken);

		await service.DrainAsync(cancellationToken);

		var job = Assert.Single(await storage.QueryJobsAsync(new() { JobHandle = handle }, cancellationToken));

		Assert.Equal(JobState.Succeeded, job.State);
		Assert.Equal(1, job.Attempt);
		Assert.Equal(["telemetry-failure"], state.Events);
	}
}

using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests;

public sealed class JobSchedulingServiceTests
{
	[Fact]
	public async Task TelemetryPersistenceFailureDoesNotConsumeAnAttempt()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var inner = new InMemoryJobStorage(timeProvider);
		await using var proxy = ControllableJobStorageProxy.Create(inner);
		((ControllableJobStorageProxy)(object)proxy).FailTelemetry = true;
		var state = new BatchWorkflowState();
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IJobStorage>(proxy);
		_ = services.AddSingleton(state);
		_ = services.AddSingleton(new DynamicExpansionState());
		_ = services.AddSingleton(new ExecutionBufferProbeState());
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddImmediateJobsCore();
		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
		await using var provider = services.BuildServiceProvider();
		var scheduler = provider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var service = provider.GetRequiredService<JobSchedulingService>();
		var handle = await scheduler.EnqueueAsync(new("telemetry-failure"), cancellationToken);

		await service.DrainAsync(cancellationToken);

		var job = Assert.Single(await inner.QueryJobsAsync(new() { JobId = handle }, cancellationToken));
		Assert.Equal(JobState.Succeeded, job.State);
		Assert.Equal(1, job.Attempt);
		Assert.Equal(["telemetry-failure"], state.Events);
	}

	[Fact]
	public async Task UnknownAcquiredJobIsFailedWithoutAnInvoker()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var inner = new InMemoryJobStorage(timeProvider);
		await using var proxy = ControllableJobStorageProxy.Create(inner);
		var proxyState = (ControllableJobStorageProxy)(object)proxy;
		proxyState.CaptureFailures = true;
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IJobStorage>(proxy);
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddImmediateJobsCore();
		await using var provider = services.BuildServiceProvider();
		var service = provider.GetRequiredService<JobSchedulingService>();
		var now = timeProvider.GetUtcNow();

		await service.ExecuteSingleAsync(
			new()
			{
				JobId = JobHandle.FromString("unknown-job"),
				JobName = "unknown-definition",
				Payload = "{}",
				State = JobState.Active,
				DueAt = now,
				CreatedAt = now,
			},
			cancellationToken
		);

		Assert.Equal("unknown-job", proxyState.CapturedFailedJobId);
		var failure = Assert.IsType<string>(proxyState.CapturedFailure);
		Assert.Contains("No generated job definition", failure, StringComparison.Ordinal);
		Assert.Contains("unknown-definition", failure, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HostShutdownDuringTelemetryKeepsCancellationSemantics()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var inner = new InMemoryJobStorage(timeProvider);
		await using var proxy = ControllableJobStorageProxy.Create(inner);
		using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		((ControllableJobStorageProxy)(object)proxy).CancelTelemetry = shutdown;
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IJobStorage>(proxy);
		_ = services.AddSingleton(new BatchWorkflowState());
		_ = services.AddSingleton(new DynamicExpansionState());
		_ = services.AddSingleton(new ExecutionBufferProbeState());
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddImmediateJobsCore();
		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
		await using var provider = services.BuildServiceProvider();
		var scheduler = provider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var service = provider.GetRequiredService<JobSchedulingService>();
		var handle = await scheduler.EnqueueAsync(new("host-shutdown"), cancellationToken);

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => service.DrainAsync(shutdown.Token).AsTask()
		);

		var job = Assert.Single(await inner.QueryJobsAsync(new() { JobId = handle }, cancellationToken));
		Assert.Equal(JobState.Active, job.State);
		Assert.Equal(1, job.Attempt);
	}

}

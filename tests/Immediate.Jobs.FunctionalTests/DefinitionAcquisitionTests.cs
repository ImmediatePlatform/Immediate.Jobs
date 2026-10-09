using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Immediate.Jobs.FunctionalTests;

public sealed class DefinitionAcquisitionTests
{
	[Fact]
	public async Task GeneratedSchedulerPausesDefinitionAndPreservesRateLimitsAcrossResumeAndRestart()
	{
		var token = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await harness.DrainAsync(TestContext.Current.CancellationToken);
		await using var firstScope = harness.Services.CreateAsyncScope();
		var scheduler = firstScope.ServiceProvider.GetRequiredService<LimitedAcquisitionJob.Scheduler>();
		var fixedBoundary = harness.TimeProvider.GetUtcNow().AddMinutes(1);
		await scheduler.PauseJobAsync(token);
		var first = await scheduler.EnqueueAsync(new(), token);
		await harness.DrainAsync(token);
		Assert.Equal(JobState.Pending, (await harness.GetJobAsync(first, token)).State);
		Assert.Equal(JobAcquisitionStatus.Paused, (await scheduler.GetAcquisitionStateAsync(token)).AcquisitionStatus);

		harness.ResetScheduler();
		await using var resumedScope = harness.Services.CreateAsyncScope();
		scheduler = resumedScope.ServiceProvider.GetRequiredService<LimitedAcquisitionJob.Scheduler>();
		Assert.True((await scheduler.GetAcquisitionStateAsync(token)).IsPaused);
		await scheduler.ResumeJobAsync(token);
		await harness.DrainAsync(token);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(first, token)).State);

		var second = await scheduler.EnqueueAsync(new(), token);
		await scheduler.PauseJobAsync(token);
		await scheduler.ResumeJobAsync(token);
		await harness.DrainAsync(token);
		Assert.Equal(JobState.Pending, (await harness.GetJobAsync(second, token)).State);
		Assert.Equal(0, (await harness.GetJobAsync(second, token)).Attempt);
		Assert.Equal(JobAcquisitionStatus.RateLimited, (await scheduler.GetAcquisitionStateAsync(token)).AcquisitionStatus);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(3), token);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(second, token)).State);
		Assert.Equal(fixedBoundary,
			(await scheduler.GetAcquisitionStateAsync(token)).NextEligibleAt);
	}

	[Fact]
	public async Task GeneratedDefinitionPersistsBothWindowPeriodsAndSharedConcurrencyConfiguration()
	{
		await using var harness = CreateHarness();
		await harness.DrainAsync(TestContext.Current.CancellationToken);
		var definition = (await harness.Storage.GetJobDefinitionsAsync(TestContext.Current.CancellationToken)).Single(static definition => string.Equals(definition.Name, "limited-acquisition", StringComparison.Ordinal));
		Assert.Equal(1, definition.SlidingWindowMax);
		Assert.Equal(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(3), definition.SlidingWindowPeriod);
		Assert.Equal(2, definition.FixedWindowMax);
		Assert.Equal(TimeSpan.FromMinutes(1), definition.FixedWindowPeriod);
		Assert.Equal(2, definition.MaxConcurrency);
		definition.AcquisitionLimits.Validate();
	}

	[Fact]
	public async Task SchedulerReadsCurrentStoredDefinitionForLimitsAndConcurrency()
	{
		var token = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(services =>
		{
			services.RemoveAll<JobDefinition>();
			_ = services.AddSingleton<JobDefinition>(provider => LimitedAcquisitionJob.CreateJobDefinition(provider));
		});
		await harness.DrainAsync(token);
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<LimitedAcquisitionJob.Scheduler>();
		var original = Assert.Single(await harness.Storage.GetJobDefinitionsAsync(token));
		var definition = original with
		{
			QueueName = "stored-queue",
			MaxAttempts = 7,
			SlidingWindowMax = 3,
			SlidingWindowPeriod = TimeSpan.FromSeconds(5),
			FixedWindowMax = 3,
			MaxConcurrency = 3,
		};
		await harness.Storage.MergeJobDefinitionsListAsync(new() { Definitions = [definition] }, token);
		var first = await scheduler.EnqueueAsync(new(), token);
		Assert.Equal(definition.QueueName, (await harness.GetJobAsync(first, token)).QueueName);
		_ = await scheduler.EnqueueAsync(new(), token);
		var acquired = await harness.Storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "monitor-worker",
			Lease = TimeSpan.FromSeconds(30),
			BatchSize = 2,
			Queues = [new() { QueueName = definition.QueueName, Capacity = 2, JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [definition.Name] = 2 } }],
			JobLimits = new Dictionary<string, JobAcquisitionLimits>(StringComparer.OrdinalIgnoreCase) { [definition.Name] = definition.AcquisitionLimits },
		}, token);
		Assert.Equal(2, acquired.Count);
		await scheduler.PauseJobAsync(token);
		await scheduler.ResumeJobAsync(token);
		var state = await scheduler.GetAcquisitionStateAsync(token);
		Assert.Equal(JobAcquisitionStatus.Ready, state.AcquisitionStatus);
		Assert.Equal(2, state.ActiveCount);
		Assert.False(state.IsConcurrencyLimited);

		// The same scheduler must read metadata again after the stored limits change.
		definition = definition with { MaxConcurrency = 1, SlidingWindowMax = 2 };
		await harness.Storage.MergeJobDefinitionsListAsync(new() { Definitions = [definition] }, token);
		await scheduler.PauseJobAsync(token);
		await scheduler.ResumeJobAsync(token);
		state = await scheduler.GetAcquisitionStateAsync(token);
		Assert.False(state.IsPaused);
		Assert.True(state.IsConcurrencyLimited);
		Assert.Equal(JobAcquisitionStatus.RateLimited, state.AcquisitionStatus);

	}

	[Fact]
	public async Task SchedulerControlsRejectDefinitionsMissingFromStorage()
	{
		var token = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<LimitedAcquisitionJob.Scheduler>();
		await Assert.ThrowsAsync<KeyNotFoundException>(async () => await scheduler.PauseJobAsync(token));
		await Assert.ThrowsAsync<KeyNotFoundException>(async () => await scheduler.ResumeJobAsync(token));
		await Assert.ThrowsAsync<KeyNotFoundException>(async () => await scheduler.GetAcquisitionStateAsync(token));
		Assert.False((await harness.Storage.GetJobAcquisitionStateAsync(scheduler.JobName, new(), token)).IsPaused);
	}

	private static JobTestHarness CreateHarness(Action<IServiceCollection>? configure = null) => new(services =>
	{
		_ = services.AddSingleton(new ExecutionState());
		_ = services.AddSingleton(new ContextProbe());
		_ = services.AddScoped<PropagationScopeState>();
		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
		configure?.Invoke(services);
	});
}

[Handler, Job(Name = "limited-acquisition", SlidingWindowMax = 1, SlidingWindowPeriod = "00:00:01.0000003",
	FixedWindowMax = 2, FixedWindowPeriod = "00:01:00", MaxConcurrency = 2)]
public static partial class LimitedAcquisitionJob
{
	private static ValueTask HandleAsync(EmptyJobRequest _, CancellationToken __) => ValueTask.CompletedTask;
}

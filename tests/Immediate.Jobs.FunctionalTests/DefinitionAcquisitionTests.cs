using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
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
	public async Task SchedulerAndMonitorReadCurrentStoredDefinitionForLimitsAndConcurrency()
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
		var monitor = harness.Services.GetRequiredService<IJobMonitor>();
		Assert.Equal(definition.MaxAttempts, (await monitor.GetJobAsync(first, token))!.MaxAttempts);
		var metadata = Assert.Single((await monitor.GetDefinitionsAsync(token)).Jobs);
		Assert.Equal(definition.AcquisitionLimits, metadata.AcquisitionLimits);
		Assert.Equal(state, Assert.Single((await monitor.GetSnapshotAsync(token)).DefinitionStatuses));
		await monitor.PauseDefinitionAsync(definition.Name.ToUpperInvariant(), token);
		Assert.True((await scheduler.GetAcquisitionStateAsync(token)).IsPaused);
		await monitor.ResumeDefinitionAsync(definition.Name.ToUpperInvariant(), token);
		Assert.False((await scheduler.GetAcquisitionStateAsync(token)).IsPaused);
		await Assert.ThrowsAsync<KeyNotFoundException>(async () => await monitor.PauseDefinitionAsync("missing-definition", token));
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

	[Fact]
	public async Task MonitorShowsPauseAndConcurrencyTogetherAndDropsExpiredLeases()
	{
		var token = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await harness.DrainAsync(TestContext.Current.CancellationToken);
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<LimitedAcquisitionJob.Scheduler>();
		_ = await scheduler.EnqueueAsync(new(), token);
		var definition = (await harness.Storage.GetJobDefinitionsAsync(TestContext.Current.CancellationToken)).Single(static definition => string.Equals(definition.Name, "limited-acquisition", StringComparison.Ordinal));
		_ = await harness.Storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "monitor-worker",
			Lease = TimeSpan.FromSeconds(30),
			BatchSize = 1,
			Queues = [new() { QueueName = definition.QueueName, Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [definition.Name] = 1 } }],
			JobLimits = new Dictionary<string, JobAcquisitionLimits>(StringComparer.Ordinal) { [definition.Name] = definition.AcquisitionLimits },
		}, token);
		await scheduler.PauseJobAsync(token);
		await harness.Services.GetRequiredService<JobSchedulingService>().DrainAsync(token);
		var monitor = harness.Services.GetRequiredService<JobMonitor>();
		var status = (await monitor.GetSnapshotAsync(token)).DefinitionStatuses.Single(state => string.Equals(state.JobName, definition.Name, StringComparison.OrdinalIgnoreCase));
		Assert.NotNull(status);
		Assert.True(status.IsPaused);
		Assert.Equal(JobAcquisitionStatus.Paused, status.AcquisitionStatus);
		Assert.Equal(1, status.ActiveCount);
		Assert.Equal(2, definition.AcquisitionLimits.MaxConcurrency);
		harness.TimeProvider.Advance(TimeSpan.FromSeconds(30));
		Assert.Equal(0, (await monitor.GetSnapshotAsync(token)).DefinitionStatuses.Single(state => string.Equals(state.JobName, definition.Name, StringComparison.OrdinalIgnoreCase)).ActiveCount);
		Assert.True((await monitor.GetSnapshotAsync(token)).DefinitionStatuses.Single(state => string.Equals(state.JobName, definition.Name, StringComparison.OrdinalIgnoreCase)).IsPaused);
	}

	[Fact]
	public async Task MonitorUsesStoredInformationForJobsAndRecurringTriggersWithoutLocalDefinitions()
	{
		var token = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness(static services => services.RemoveAll<JobDefinition>());
		await harness.DrainAsync(token);
		await harness.Storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = "Remote-Job", QueueName = "current-queue", MaxAttempts = 7 }],
		}, token);
		await harness.Storage.UpsertRecurringAsync(new()
		{
			Name = "remote-recurring",
			JobName = "remote-job",
			QueueName = "previous-queue",
			Cron = "* * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = harness.TimeProvider.GetUtcNow().AddMinutes(1),
		}, token);
		var monitor = harness.Services.GetRequiredService<JobMonitor>();
		await monitor.TriggerRecurringAsync("remote-recurring", token);
		var job = Assert.Single(await harness.Storage.QueryJobsAsync(new(), token));
		Assert.Equal("Remote-Job", job.JobName);
		Assert.Equal("current-queue", job.QueueName);
		Assert.Equal(7, (await monitor.GetJobAsync(job.JobHandle, token))!.MaxAttempts);
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
	FixedWindowMax = 2,
	FixedWindowPeriod = "00:01:00",
	MaxConcurrency = 2)]
public static partial class LimitedAcquisitionJob
{
	private static ValueTask HandleAsync(EmptyJobRequest _, CancellationToken __) => ValueTask.CompletedTask;
}

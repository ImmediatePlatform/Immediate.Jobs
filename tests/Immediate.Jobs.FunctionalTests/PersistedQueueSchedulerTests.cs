using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.FunctionalTests;

public sealed class PersistedQueueSchedulerTests
{
	[Fact]
	public async Task SchedulerUsesCurrentPersistedQueueAndRetainsSubmittedRouting()
	{
		var token = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await harness.DrainAsync(token);
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<RecordMessageJob.Scheduler>();
		await ChangeQueueAsync(harness, scheduler.JobName, "persisted-priority", token);

		var first = await scheduler.EnqueueAsync(new("first"), token);
		Assert.Equal("persisted-priority", (await harness.GetJobAsync(first, token)).QueueName);
		await ChangeQueueAsync(harness, scheduler.JobName.ToUpperInvariant(), "updated-priority", token);
		var second = await scheduler.ScheduleAsync(new("second"), TimeSpan.FromSeconds(1), token);
		var waiting = await scheduler.WaitForTriggerAsync(new RecordMessageJob.Payload("waiting"), token);
		Assert.Equal("updated-priority", (await harness.GetJobAsync(second, token)).QueueName);
		Assert.Equal("updated-priority", (await harness.GetJobAsync(waiting, token)).QueueName);
		Assert.Equal(scheduler.JobName, (await harness.GetJobAsync(second, token)).JobName);
		Assert.Equal("persisted-priority", (await harness.GetJobAsync(first, token)).QueueName);

		await harness.Storage.MergeJobDefinitionsListAsync(new() { Definitions = [] }, token);
		await Assert.ThrowsAsync<KeyNotFoundException>(() => scheduler.EnqueueAsync(new("unknown"), token).AsTask());
		Assert.Equal(3, (await harness.Storage.QueryJobsAsync(new(), token)).Count);
		Assert.Equal("persisted-priority", (await harness.GetJobAsync(first, token)).QueueName);
	}

	[Fact]
	public async Task BatchResolvesBufferedJobsWhenCommittedAndRejectsMissingDefinitionsBeforeWriting()
	{
		var token = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await harness.DrainAsync(token);
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<RecordMessageJob.Scheduler>();
		var other = scope.ServiceProvider.GetRequiredService<PlainRequestJob.Scheduler>();
		await using var batch = harness.Batches.Begin();
		var parent = scheduler.Enqueue(new("parent"), batch);
		var child = scheduler.ScheduleAfter(new("child"), parent);
		await ChangeQueueAsync(harness, scheduler.JobName, "commit-queue", token);
		await batch.CommitAsync(token);
		Assert.Equal("commit-queue", (await harness.GetJobAsync(parent.JobHandle, token)).QueueName);
		Assert.Equal("commit-queue", (await harness.GetJobAsync(child.JobHandle, token)).QueueName);

		await using var rejected = harness.Batches.Begin();
		scheduler.Enqueue(new("known"), rejected);
		other.Enqueue(new("missing"), rejected);
		var snapshot = await harness.Storage.GetMonitoringSnapshotAsync(token);
		await harness.Storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = (await harness.Storage.GetJobDefinitionsAsync(token)).Where(definition => !string.Equals(definition.Name, other.JobName, StringComparison.OrdinalIgnoreCase)).ToList(),
			RecurringSchedules = snapshot.Recurring.Where(static schedule => schedule.IsCodeDefined).ToList(),
		}, token);
		await Assert.ThrowsAsync<KeyNotFoundException>(() => rejected.CommitAsync(token).AsTask());
		Assert.Null(await harness.Storage.GetBatchStatusAsync(rejected.BatchHandle, token));
		Assert.Equal(2, (await harness.Storage.QueryJobsAsync(new(), token)).Count);
	}

	[Fact]
	public async Task DynamicRecurringScheduleUsesPersistedQueue()
	{
		var token = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await harness.DrainAsync(token);
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<TimeoutJob.Scheduler>();
		var snapshot = await harness.Storage.GetMonitoringSnapshotAsync(token);
		await harness.Storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [(await harness.Storage.GetJobDefinitionsAsync(token)).Single(definition => string.Equals(definition.Name, scheduler.JobName, StringComparison.OrdinalIgnoreCase)) with { QueueName = "recurring-queue" }],
		}, token);
		await scheduler.AddOrUpdateRecurringAsync("dynamic-queue", "* * * * *", "UTC", token);
		Assert.Equal("recurring-queue", (await harness.Storage.GetMonitoringSnapshotAsync(token)).Recurring.Single(static schedule => string.Equals(schedule.Name, "dynamic-queue", StringComparison.Ordinal)).QueueName);
	}

	private static JobTestHarness CreateHarness() => new(static services =>
	{
		services.AddSingleton(new ExecutionState());
		services.AddSingleton(new ContextProbe());
		services.AddScoped<PropagationScopeState>();
		services.AddImmediateJobsFunctionalTestsHandlers();
		services.AddImmediateJobsFunctionalTestsJobs();
	});

	private static async ValueTask ChangeQueueAsync(JobTestHarness harness, string jobName, string queueName, CancellationToken token)
	{
		var snapshot = await harness.Storage.GetMonitoringSnapshotAsync(token);
		await harness.Storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = (await harness.Storage.GetJobDefinitionsAsync(token)).Select(definition => string.Equals(definition.Name, jobName, StringComparison.OrdinalIgnoreCase)
				? definition with { Name = jobName, QueueName = queueName } : definition).ToList(),
			RecurringSchedules = snapshot.Recurring.Where(static schedule => schedule.IsCodeDefined).ToList(),
		}, token);
	}
}

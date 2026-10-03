using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.FunctionalTests;

public sealed class WaitForTriggerTests
{
	[Fact]
	public async Task JobWithoutParametersCannotBeTriggeredUntilParametersAreSupplied()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		var handle = await scheduler.WaitForTriggerAsync(groupId: "tenant-a", cancellationToken);

		Assert.Equal("tenant-a", (await harness.GetJobAsync(handle, cancellationToken)).GroupId);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => scheduler.TriggerAsync(handle, cancellationToken).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => scheduler.TryTriggerAsync(handle, cancellationToken).AsTask());

		await scheduler.UpdateParametersAsync(handle, new("supplied"), cancellationToken);
		await scheduler.TriggerAsync(handle, cancellationToken);
		await harness.DrainAsync(cancellationToken);

		Assert.Equal(["supplied"], state.Events);
	}

	[Fact]
	public async Task TriggerThrowsWhereTryTriggerReturnsFalseForJobsNoLongerWaiting()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var handle = await scheduler.WaitForTriggerAsync(new BatchWorkflowJob.Payload("once"), cancellationToken);

		Assert.True(await scheduler.TryTriggerAsync(handle, cancellationToken));
		Assert.False(await scheduler.TryTriggerAsync(handle, cancellationToken));
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => scheduler.TriggerAsync(handle, cancellationToken).AsTask());
	}

	[Fact]
	public async Task TriggerDelayIsMeasuredFromTheTrigger()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var handle = await scheduler.WaitForTriggerAsync(new BatchWorkflowJob.Payload("delayed"), cancellationToken);

		harness.TimeProvider.Advance(TimeSpan.FromMinutes(10));
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => scheduler.TriggerAsync(handle, TimeSpan.FromMinutes(-1), cancellationToken).AsTask());
		await scheduler.TriggerAsync(handle, TimeSpan.FromMinutes(5), cancellationToken);

		var scheduled = await harness.GetJobAsync(handle, cancellationToken);
		Assert.Equal(JobState.Scheduled, scheduled.State);
		Assert.Equal(harness.TimeProvider.GetUtcNow().AddMinutes(5), scheduled.DueAt);
	}

	[Fact]
	public async Task WaitingContinuationRunsAfterBothItsParentAndItsTrigger()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		var parent = await scheduler.EnqueueAsync(new("parent"), cancellationToken);
		var child = await scheduler.WaitForTriggerAsync(new BatchWorkflowJob.Payload("child"), [parent], cancellationToken: cancellationToken);
		await harness.DrainAsync(cancellationToken);

		Assert.Equal(["parent"], state.Events);
		Assert.Equal(JobState.WaitingForTrigger, (await harness.GetJobAsync(child, cancellationToken)).State);

		await scheduler.TriggerAsync(child, cancellationToken);
		await harness.DrainAsync(cancellationToken);

		Assert.Equal(["parent", "child"], state.Events);
	}

	[Fact]
	public async Task WaitingBatchRunsOnlyAfterTheBatchIsTriggered()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		await using var batch = batches.Begin();
		var root = scheduler.Enqueue(new("root"), batch);
		var chain = scheduler.ScheduleAfter(new("chain"), root);
		var batchHandle = await batch.CommitWaitingForTriggerAsync(cancellationToken);
		await harness.DrainAsync(cancellationToken);

		var graphStorage = Assert.IsAssignableFrom<IJobGraphStorage>(harness.Storage);
		Assert.Equal(BatchState.WaitingForTrigger, (await graphStorage.GetBatchStatusAsync(batchHandle, cancellationToken))!.State);
		Assert.Equal(JobState.WaitingForTrigger, (await harness.GetJobAsync(root.JobHandle, cancellationToken)).State);
		Assert.Empty(state.Events);

		await batches.TriggerAsync(batchHandle, cancellationToken);
		await harness.DrainAsync(cancellationToken);

		Assert.Equal(["root", "chain"], state.Events);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(chain.JobHandle, cancellationToken)).State);
		Assert.False(await batches.TryTriggerAsync(batchHandle, cancellationToken));
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => batches.TriggerAsync(batchHandle, cancellationToken).AsTask());
	}

	private static JobTestHarness CreateHarness(BatchWorkflowState? state = null) => new(services =>
	{
		_ = services.AddSingleton(state ?? new());
		_ = services.AddSingleton(new DynamicExpansionState());
		_ = services.AddSingleton(new ExecutionBufferProbeState());
		_ = services.AddSingleton(new ExecutionState());
		_ = services.AddSingleton(new ContextProbe());
		_ = services.AddScoped<PropagationScopeState>();
		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
	});
}

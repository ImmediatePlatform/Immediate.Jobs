using Immediate.Handlers.Shared;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;

namespace Immediate.Jobs.FunctionalTests;

#pragma warning disable CS1591
public sealed class BatchesAndContinuationsTests
{
	[Fact]
	public async Task TypedSchedulerReturnsJobHandle()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		JobHandle handle = await scheduler.EnqueueAsync(new("one"), cancellationToken);

		Assert.False(string.IsNullOrWhiteSpace(handle.Id));
		Assert.True(Guid.TryParseExact(handle.Id, "N", out _));
		Assert.Equal(handle.Id, (await harness.GetJobAsync(handle.Id, cancellationToken)).Id);
		Assert.Equal(handle, new JobHandle(handle.Id));
	}

	[Fact]
	public async Task BatchBuffersUntilCommitAndDisposalRollsBackUncommittedJobs()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		JobHandle rolledBack;
		await using (var batch = batches.Begin())
		{
			rolledBack = scheduler.AddToBatch(
				batch,
				new("rolled-back")
			);
			Assert.Empty(await harness.QueryJobsAsync(cancellationToken: cancellationToken));
		}

		Assert.Empty(await harness.QueryJobsAsync(cancellationToken: cancellationToken));

		await using var committedBatch = batches.Begin();
		var committed = scheduler.AddToBatch(
			committedBatch,
			new("committed")
		);
		Assert.Empty(await harness.QueryJobsAsync(cancellationToken: cancellationToken));

		var batchHandle = await committedBatch.CommitAsync(cancellationToken);

		Assert.False(string.IsNullOrWhiteSpace(batchHandle.Id));
		Assert.True(Guid.TryParseExact(batchHandle.Id, "N", out _));
		Assert.NotEqual(rolledBack.Id, committed.Id);
		var job = Assert.Single(await harness.QueryJobsAsync(cancellationToken: cancellationToken));
		Assert.Equal(committed.Id, job.Id);
		Assert.Equal(batchHandle.Id, job.BatchId);
		Assert.Equal(JobState.Pending, job.State);
	}

	[Fact]
	public async Task BatchChainFanOutAndFanInReleaseInDependencyOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();

		var root = scheduler.AddToBatch(batch, new("root"));
		var chain = await scheduler.ScheduleAfterAsync(root, new("chain"), cancellationToken: cancellationToken);
		var fanA = await scheduler.ScheduleAfterAsync(root, new("fan-a"), cancellationToken: cancellationToken);
		var fanB = await scheduler.ScheduleAfterAsync(root, new("fan-b"), cancellationToken: cancellationToken);
		var join = await scheduler.ScheduleAfterAsync(
			[fanA, fanB],
			new("join"),
			cancellationToken: cancellationToken
		);
		_ = await batch.CommitAsync(cancellationToken);

		Assert.Equal(JobState.Pending, (await harness.GetJobAsync(root.Id, cancellationToken)).State);
		Assert.Equal(JobState.AwaitingContinuation, (await harness.GetJobAsync(chain.Id, cancellationToken)).State);
		Assert.Equal(2, (await harness.GetJobAsync(join.Id, cancellationToken)).RemainingDependencies);

		await harness.DrainAsync(cancellationToken);

		Assert.All(
			await harness.QueryJobsAsync(cancellationToken: cancellationToken),
			static job => Assert.Equal(JobState.Succeeded, job.State)
		);
		AssertBefore(state.Events, "root", "chain");
		AssertBefore(state.Events, "root", "fan-a");
		AssertBefore(state.Events, "root", "fan-b");
		AssertBefore(state.Events, "fan-a", "join");
		AssertBefore(state.Events, "fan-b", "join");
	}

	[Fact]
	public async Task FailedParentCancelsSuccessChildButReleasesFailureAndCompleteChildren()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();

		var parent = scheduler.AddToBatch(
			batch,
			new("parent", Fail: true)
		);
		var successOnly = await scheduler.ScheduleAfterAsync(parent, new("success-only"), cancellationToken: cancellationToken);
		var failureOnly = await scheduler.ScheduleAfterAsync(
			parent,
			new("failure-only"),
			ContinuationTrigger.Failure,
			cancellationToken: cancellationToken
		);
		var always = await scheduler.ScheduleAfterAsync(
			parent,
			new("always"),
			ContinuationTrigger.Complete,
			cancellationToken: cancellationToken
		);
		_ = await batch.CommitAsync(cancellationToken);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(JobState.Failed, (await harness.GetJobAsync(parent.Id, cancellationToken)).State);
		Assert.Equal(JobState.Cancelled, (await harness.GetJobAsync(successOnly.Id, cancellationToken)).State);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(failureOnly.Id, cancellationToken)).State);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(always.Id, cancellationToken)).State);
		Assert.Equal(["always", "failure-only", "parent"], state.Events.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task FailedBatchReleasesFailureAndCompleteContinuations()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		_ = scheduler.AddToBatch(
			batch,
			new("batch-parent", Fail: true)
		);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		var successOnly = await scheduler.ScheduleAfterAsync(
			batchHandle,
			new("batch-success"),
			ContinuationTrigger.Success,
			cancellationToken: cancellationToken
		);
		var failureOnly = await scheduler.ScheduleAfterAsync(
			batchHandle,
			new("batch-failure"),
			ContinuationTrigger.Failure,
			cancellationToken: cancellationToken
		);
		var always = await scheduler.ScheduleAfterAsync(
			batchHandle,
			new("batch-complete"),
			ContinuationTrigger.Complete,
			cancellationToken: cancellationToken
		);

		await harness.DrainAsync(cancellationToken);

		var graphStorage = Assert.IsAssignableFrom<IJobGraphStorage>(harness.Storage);
		Assert.Equal(
			BatchState.Failed,
			(await graphStorage.GetBatchStatusAsync(batchHandle.Id, cancellationToken))!.State
		);
		Assert.Equal(JobState.Cancelled, (await harness.GetJobAsync(successOnly, cancellationToken)).State);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(failureOnly, cancellationToken)).State);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(always, cancellationToken)).State);
		Assert.Equal(["batch-complete", "batch-failure", "batch-parent"], state.Events.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task StandaloneContinuationEvaluatesAlreadyTerminalParentImmediately()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var parent = await scheduler.EnqueueAsync(new("parent"), cancellationToken);
		await harness.DrainAsync(cancellationToken);

		var child = await scheduler.ScheduleAfterAsync(parent, new("child"), cancellationToken: cancellationToken);

		Assert.Equal(JobState.Pending, (await harness.GetJobAsync(child.Id, cancellationToken)).State);
		await harness.DrainAsync(cancellationToken);
		Assert.Equal(["parent", "child"], state.Events);
	}

	[Fact]
	public async Task BatchMonitoringReportsProgressMembersGraphAndIncomingEdges()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var monitor = scope.ServiceProvider.GetRequiredService<IJobBatchMonitor>();
		var jobMonitor = scope.ServiceProvider.GetRequiredService<IJobMonitor>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		var parent = scheduler.AddToBatch(batch, new("parent"));
		var child = await scheduler.ScheduleAfterAsync(parent, new("child"), cancellationToken: cancellationToken);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		var initial = Assert.IsType<BatchStatus>(await monitor.GetStatusAsync(batchHandle.Id, cancellationToken));
		Assert.Equal(BatchState.Executing, initial.State);
		Assert.Equal(2, initial.Total);
		Assert.Equal(2, initial.Remaining);
		Assert.Equal(0, initial.FractionSettled);
		Assert.Equal(
			[child.Id],
			(await monitor.QueryMembersAsync(
				batchHandle.Id,
				new() { State = JobState.AwaitingContinuation },
				cancellationToken
			)).Select(static member => member.JobId)
		);

		var graph = Assert.IsType<BatchGraph>(await monitor.GetGraphAsync(batchHandle.Id, cancellationToken));
		Assert.Equal(2, graph.Nodes.Count);
		var edge = Assert.Single(graph.Edges);
		Assert.Equal(parent.Id, edge.ParentJobId);
		Assert.Equal(child.Id, edge.ChildJobId);
		var childStatus = Assert.IsType<JobStatus>(await jobMonitor.GetJobAsync(child.Id, cancellationToken));
		Assert.Equal(batchHandle.Id, childStatus.BatchId);
		_ = Assert.Single(childStatus.DependsOn);

		await harness.DrainAsync(cancellationToken);

		var completed = Assert.IsType<BatchStatus>(await monitor.GetStatusAsync(batchHandle.Id, cancellationToken));
		Assert.Equal(BatchState.Succeeded, completed.State);
		Assert.Equal(2, completed.Succeeded);
		Assert.Equal(0, completed.Remaining);
		Assert.Equal(1, completed.FractionSettled);
		_ = Assert.NotNull(completed.StartedAt);
		_ = Assert.NotNull(completed.CompletedAt);
	}

	[Fact]
	public async Task FailedAttemptDiscardsMidJobBufferAndSuccessfulRetrySplicesOnce()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var workflow = new BatchWorkflowState();
		var expansion = new DynamicExpansionState { FailuresRemaining = 1 };
		await using var harness = CreateHarness(workflow, expansion);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var expanding = scope.ServiceProvider.GetRequiredService<DynamicExpansionJob.Scheduler>();
		var workflowScheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		var current = expanding.AddToBatch(batch, new());
		var waiter = await workflowScheduler.ScheduleAfterAsync(
			current,
			new("original-waiter"),
			cancellationToken: cancellationToken
		);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(JobState.Scheduled, (await harness.GetJobAsync(current, cancellationToken)).State);
		Assert.Equal(JobState.AwaitingContinuation, (await harness.GetJobAsync(waiter, cancellationToken)).State);
		Assert.Equal(2, (await harness.QueryJobsAsync(cancellationToken: cancellationToken)).Count);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromSeconds(1), cancellationToken);

		var jobs = (await harness.QueryJobsAsync(cancellationToken: cancellationToken))
			.Where(job => job.BatchId == batchHandle.Id)
			.ToArray();
		Assert.Equal(
			["batch-workflow-test", "batch-workflow-test", "dynamic-expansion-test"],
			jobs.Select(static job => job.JobName).Order(StringComparer.Ordinal)
		);
		Assert.All(jobs, static job => Assert.Equal(JobState.Succeeded, job.State));
		Assert.Equal(["inserted", "original-waiter"], workflow.Events);
		Assert.Equal(2, expansion.Attempts);
	}

	[Fact]
	public async Task RuntimeRegistersScopedBatchAndMonitoringServices()
	{
		var services = new ServiceCollection();
		_ = services.AddImmediateJobsCore();
		await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

		await using var firstScope = provider.CreateAsyncScope();
		var scheduler = firstScope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var batchMonitor = firstScope.ServiceProvider.GetRequiredService<IJobBatchMonitor>();
		var jobMonitor = firstScope.ServiceProvider.GetRequiredService<IJobMonitor>();
		await using var secondScope = provider.CreateAsyncScope();

		_ = Assert.IsType<JobBatchScheduler>(scheduler);
		Assert.Same(batchMonitor, jobMonitor);
		Assert.NotSame(scheduler, secondScope.ServiceProvider.GetRequiredService<IJobBatchScheduler>());
	}

	private static JobTestHarness CreateHarness(
		BatchWorkflowState? state = null,
		DynamicExpansionState? expansion = null
	) => new(services =>
	{
		_ = services.AddSingleton(state ?? new());
		_ = services.AddSingleton(expansion ?? new());
		_ = services.AddSingleton(new ExecutionState());
		_ = services.AddSingleton(new ContextProbe());
		_ = services.AddScoped<PropagationScopeState>();
		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsBehaviors();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
	});

	private static void AssertBefore(IReadOnlyList<string> events, string first, string second)
	{
		var observed = events.ToArray();
		Assert.True(
			Array.IndexOf(observed, first) < Array.IndexOf(observed, second),
			$"Expected '{first}' before '{second}', but observed: {string.Join(", ", events)}"
		);
	}
}

public sealed class BatchWorkflowState
{
	public Collection<string> Events { get; } = [];
}

public sealed class DynamicExpansionState
{
	public int FailuresRemaining { get; set; }
	public int Attempts { get; set; }
}

[Handler, Job(Name = "batch-workflow-test", MaxAttempts = 1)]
public sealed partial class BatchWorkflowJob(BatchWorkflowState? state = null)
{
	public sealed record Payload(string Name, bool Fail = false);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		state?.Events.Add(payload.Name);
		if (payload.Fail)
			throw new InvalidOperationException("Expected workflow test failure.");
		return ValueTask.CompletedTask;
	}
}

[Handler, Job(Name = "dynamic-expansion-test", MaxAttempts = 2, Backoff = BackoffStrategy.Fixed, BackoffBase = "00:00:01")]
public sealed partial class DynamicExpansionJob(
	BatchWorkflowJob.Scheduler scheduler,
	DynamicExpansionState? state = null
)
{
	public sealed record Payload : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		_ = scheduler.ScheduleAfter(
			payload.JobDetails ?? throw new InvalidOperationException("Job details were not populated."),
			new("inserted"),
			ContinuationOptions.BeforeContinuations
		);
		if (state is null)
			return ValueTask.CompletedTask;
		state.Attempts++;
		if (state.FailuresRemaining > 0)
		{
			state.FailuresRemaining--;
			throw new InvalidOperationException("Expected first-attempt failure.");
		}

		return ValueTask.CompletedTask;
	}
}
#pragma warning restore CS1591

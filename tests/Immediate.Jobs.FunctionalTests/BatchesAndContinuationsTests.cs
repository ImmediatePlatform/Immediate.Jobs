using System.Collections.Concurrent;
using System.Globalization;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests;

public sealed class BatchesAndContinuationsTests
{
	[Fact]
	public void EmptyBatchProgressIsFullySettled() =>
		Assert.Equal(1d, BatchStatus.CalculateFractionSettled(total: 0, remaining: 0));

	[Fact]
	public async Task TypedSchedulerReturnsJobHandle()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		var handle = await scheduler.EnqueueAsync(new("one"), cancellationToken);

		Assert.False(string.IsNullOrWhiteSpace(handle.JobId));
		Assert.True(Guid.TryParseExact(handle.JobId, "N", out _));
		Assert.Equal(handle, (await harness.GetJobAsync(handle.JobId, cancellationToken)).JobId);
	}

	[Fact]
	public async Task TypedSchedulersCancelJobsAndCommittedBatches()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var jobHandle = await scheduler.EnqueueAsync(new("cancel-job"), cancellationToken);

		await scheduler.CancelAsync(jobHandle, cancellationToken);

		Assert.Equal(JobState.Cancelled, (await harness.GetJobAsync(jobHandle.JobId, cancellationToken)).State);

		await using var batch = batches.Begin();
		var memberHandle = scheduler.Enqueue(new("cancel-batch"), batch);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		await batches.CancelAsync(batchHandle, cancellationToken);

		Assert.Equal(JobState.Cancelled, (await harness.GetJobAsync(memberHandle.JobId, cancellationToken)).State);
		var graphStorage = Assert.IsAssignableFrom<IJobGraphStorage>(harness.Storage);
		var status = Assert.IsType<BatchStatus>(await graphStorage.GetBatchStatusAsync(batchHandle, cancellationToken));
		Assert.Equal(BatchState.Cancelled, status.State);
	}

	[Fact]
	public async Task BatchBuffersUntilCommitAndDisposalRollsBackUncommittedJobs()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		BatchJobHandle rolledBack;
		await using (var batch = batches.Begin())
		{
			rolledBack = scheduler.Enqueue(
				new("rolled-back"),
				batch
			);
			Assert.Empty(await harness.QueryJobsAsync(cancellationToken: cancellationToken));
		}

		Assert.Empty(await harness.QueryJobsAsync(cancellationToken: cancellationToken));

		await using var committedBatch = batches.Begin();
		var committed = scheduler.Enqueue(
			new("committed"),
			committedBatch
		);
		Assert.Empty(await harness.QueryJobsAsync(cancellationToken: cancellationToken));

		var batchHandle = await committedBatch.CommitAsync(cancellationToken);

		Assert.NotEqual(rolledBack.JobId, committed.JobId);
		var job = Assert.Single(await harness.QueryJobsAsync(cancellationToken: cancellationToken));
		Assert.Equal(committed.JobId, job.JobId);
		Assert.Equal(batchHandle, job.BatchId);
		Assert.Equal(JobState.Pending, job.State);
	}

	[Fact]
	public async Task ConcurrentBatchAdditionsAreSnapshottedExactlyOnce()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		var handles = new ConcurrentBag<BatchJobHandle>();

		_ = Parallel.For(0, 128, index => handles.Add(scheduler.Enqueue(new(string.Create(CultureInfo.InvariantCulture, $"job-{index}")), batch)));
		var batchHandle = await batch.CommitAsync(cancellationToken);

		var jobs = (await harness.QueryJobsAsync(cancellationToken: cancellationToken))
			.Where(job => job.BatchId == batchHandle)
			.ToArray();
		Assert.Equal(128, handles.Count);
		Assert.Equal(128, jobs.Length);
		Assert.Equal(128, jobs.Select(static job => job.JobId).Distinct().Count());
	}

	[Fact]
	public async Task GroupedBatchAdditionsUseTheSameNormalizationAsDirectScheduling()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();

		_ = scheduler.Enqueue(new("grouped"), batch, groupId: "tenant-a");
		_ = scheduler.Enqueue(new("blank"), batch, groupId: "  ");
		_ = scheduler.Schedule(new("absolute"), batch, harness.TimeProvider.GetUtcNow(), groupId: "tenant-b");
		_ = await batch.CommitAsync(cancellationToken);

		var jobs = (await harness.QueryJobsAsync(cancellationToken: cancellationToken))
			.ToDictionary(static job => job.Payload, StringComparer.Ordinal);
		Assert.Contains(jobs.Values, static job => string.Equals(job.GroupId, "tenant-a", StringComparison.Ordinal));
		Assert.Contains(jobs.Values, static job => string.Equals(job.GroupId, "tenant-b", StringComparison.Ordinal));
		Assert.Contains(jobs.Values, static job => job.GroupId is null);
	}

	[Fact]
	public async Task BatchChainFanOutAndFanInReleaseInDependencyOrder()
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
		var fanA = scheduler.ScheduleAfter(new("fan-a"), root);
		var fanB = scheduler.ScheduleAfter(new("fan-b"), root);
		var join = scheduler.ScheduleAfter(
			new("join"),
			[fanA, fanB]
		);
		_ = await batch.CommitAsync(cancellationToken);

		Assert.Equal(JobState.Pending, (await harness.GetJobAsync(root.JobId, cancellationToken)).State);
		Assert.Equal(JobState.AwaitingContinuation, (await harness.GetJobAsync(chain.JobId, cancellationToken)).State);
		Assert.Equal(2, (await harness.GetJobAsync(join.JobId, cancellationToken)).RemainingDependencies);

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
	public async Task FollowUpBatchAddsParentBatchDependencyOnlyToGraphRoots()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var monitor = scope.ServiceProvider.GetRequiredService<JobMonitor>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		await using var parentBatch = batches.Begin();
		_ = scheduler.Enqueue(new("parent"), parentBatch);
		var parentBatchHandle = await parentBatch.CommitAsync(cancellationToken);

		await using var followUpBatch = batches.Begin(parentBatchHandle, ContinuationTrigger.Complete);
		var root = scheduler.Enqueue(new("root"), followUpBatch);
		var child = scheduler.ScheduleAfter(new("child"), root);
		var followUpHandle = await followUpBatch.CommitAsync(cancellationToken);

		var graph = Assert.IsType<BatchGraph>(await monitor.GetBatchGraphAsync(followUpHandle, cancellationToken));
		Assert.Equal(2, graph.Edges.Count);
		var childEdge = Assert.Single(graph.Edges, edge => edge.ChildJobId == child.JobId);
		Assert.Equal(root.JobId, childEdge.ParentJobId);
		Assert.Null(childEdge.ParentBatchId);
		var rootEdge = Assert.Single(graph.Edges, edge => edge.ChildJobId == root.JobId);
		Assert.Null(rootEdge.ParentJobId);
		Assert.Equal(parentBatchHandle, rootEdge.ParentBatchId);
		Assert.Equal(ContinuationTrigger.Complete, rootEdge.Trigger);
		Assert.Equal(1, (await harness.GetJobAsync(root.JobId, cancellationToken)).RemainingDependencies);
		Assert.Equal(1, (await harness.GetJobAsync(child.JobId, cancellationToken)).RemainingDependencies);
	}

	[Fact]
	public async Task SuccessfulBatchSkipsFailureBranchAndStillSucceeds()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var monitor = scope.ServiceProvider.GetRequiredService<JobMonitor>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();

		var parent = scheduler.Enqueue(new("parent"), batch);
		var failureOnly = scheduler.ScheduleAfter(
			new("failure-only"),
			parent,
			ContinuationTrigger.Failure
		);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(parent.JobId, cancellationToken)).State);
		Assert.Equal(JobState.Skipped, (await harness.GetJobAsync(failureOnly.JobId, cancellationToken)).State);
		var status = Assert.IsType<BatchStatus>(await monitor.GetBatchAsync(batchHandle, cancellationToken));
		Assert.Equal(BatchState.Succeeded, status.State);
		Assert.Equal(1, status.Succeeded);
		Assert.Equal(1, status.Skipped);
		Assert.Equal(0, status.Cancelled);
		Assert.Equal(["parent"], state.Events);
	}

	[Fact]
	public async Task FailedParentSkipsSuccessChildButReleasesFailureAndCompleteChildren()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();

		var parent = scheduler.Enqueue(new("parent", Fail: true), batch);
		var successOnly = scheduler.ScheduleAfter(
			new("success-only"),
			parent
		);
		var failureOnly = scheduler.ScheduleAfter(
			new("failure-only"),
			parent,
			ContinuationTrigger.Failure
		);
		var always = scheduler.ScheduleAfter(
			new("always"),
			parent,
			ContinuationTrigger.Complete
		);
		_ = await batch.CommitAsync(cancellationToken);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(JobState.Failed, (await harness.GetJobAsync(parent.JobId, cancellationToken)).State);
		Assert.Equal(JobState.Skipped, (await harness.GetJobAsync(successOnly.JobId, cancellationToken)).State);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(failureOnly.JobId, cancellationToken)).State);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(always.JobId, cancellationToken)).State);
		Assert.Equal(["always", "failure-only", "parent"], state.Events.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task FailedBatchReleasesFailureAndCompleteContinuations()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		_ = scheduler.Enqueue(new("batch-parent", Fail: true), batch);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		var successOnly = await scheduler.ScheduleAfterAsync(
			new("batch-success"),
			batchHandle,
			ContinuationTrigger.Success,
			cancellationToken: cancellationToken
		);
		var failureOnly = await scheduler.ScheduleAfterAsync(
			new("batch-failure"),
			batchHandle,
			ContinuationTrigger.Failure,
			cancellationToken: cancellationToken
		);
		var always = await scheduler.ScheduleAfterAsync(
			new("batch-complete"),
			batchHandle,
			ContinuationTrigger.Complete,
			cancellationToken: cancellationToken
		);

		await harness.DrainAsync(cancellationToken);

		var graphStorage = Assert.IsType<IJobGraphStorage>(harness.Storage, exactMatch: false);
		Assert.Equal(
			BatchState.Failed,
			(await graphStorage.GetBatchStatusAsync(batchHandle, cancellationToken))!.State
		);
		Assert.Equal(JobState.Skipped, (await harness.GetJobAsync(successOnly, cancellationToken)).State);
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

		var child = await scheduler.ScheduleAfterAsync(new("child"), parent, cancellationToken: cancellationToken);

		Assert.Equal(JobState.Pending, (await harness.GetJobAsync(child.JobId, cancellationToken)).State);
		await harness.DrainAsync(cancellationToken);
		Assert.Equal(["parent", "child"], state.Events);
	}

	[Fact]
	public async Task StandaloneContinuationsRejectInvalidParentsBeforePersistence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var standalone = await scheduler.EnqueueAsync(new("standalone"), cancellationToken);
		await using var batch = batches.Begin();
		var batched = scheduler.Enqueue(new("batched"), batch);
		_ = await batch.CommitAsync(cancellationToken);
		await using var foreignBatch = batches.Begin();
		var foreignBatched = scheduler.Enqueue(new("foreign-batched"), foreignBatch);
		var expectedCount = (await harness.QueryJobsAsync(cancellationToken: cancellationToken)).Count;

		async Task AssertRejected(string expectedMessage, params JobHandle[] parents)
		{
			var exception = await Assert.ThrowsAsync<ImmediateJobException>(async () =>
				await scheduler.ScheduleAfterAsync(new("invalid"), parents, cancellationToken: cancellationToken));
			Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(expectedCount, (await harness.QueryJobsAsync(cancellationToken: cancellationToken)).Count);
		}

		await AssertRejected("duplicate", standalone, standalone);
		await AssertRejected("unrelated scopes", standalone, batched.JobId);
		await AssertRejected("unrelated scopes", batched.JobId, standalone);
		await AssertRejected("unrelated scopes", batched.JobId, foreignBatched.JobId);
	}

	[Fact]
	public async Task BatchMonitoringReportsProgressMembersGraphAndIncomingEdges()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var monitor = scope.ServiceProvider.GetRequiredService<JobMonitor>();
		Assert.Same(monitor, scope.ServiceProvider.GetRequiredService<IJobMonitor>());
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		var parent = scheduler.Enqueue(new("parent"), batch);
		var child = scheduler.ScheduleAfter(new("child"), parent);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		var initial = Assert.IsType<BatchStatus>(await monitor.GetBatchAsync(batchHandle, cancellationToken));
		Assert.Equal(BatchState.Executing, initial.State);
		Assert.Equal(2, initial.Total);
		Assert.Equal(2, initial.Remaining);
		Assert.Equal(0, initial.FractionSettled);
		Assert.Equal(
			[child.JobId],
			(await monitor.QueryBatchMembersAsync(
				batchHandle,
				new() { State = JobState.AwaitingContinuation },
				cancellationToken
			))?.Select(static member => member.JobId)
		);

		var graph = Assert.IsType<BatchGraph>(await monitor.GetBatchGraphAsync(batchHandle, cancellationToken));
		Assert.Equal(2, graph.Nodes.Count);
		var edge = Assert.Single(graph.Edges);
		Assert.Equal(parent.JobId, edge.ParentJobId);
		Assert.Equal(child.JobId, edge.ChildJobId);
		var childStatus = Assert.IsType<JobStatus>(await monitor.GetJobAsync(child.JobId, cancellationToken));
		Assert.Equal(batchHandle, childStatus.BatchId);
		Assert.Equal(1, childStatus.MaxAttempts);
		_ = Assert.Single(childStatus.DependsOn);

		await harness.DrainAsync(cancellationToken);

		var completed = Assert.IsType<BatchStatus>(await monitor.GetBatchAsync(batchHandle, cancellationToken));
		Assert.Equal(BatchState.Succeeded, completed.State);
		Assert.Equal(2, completed.Succeeded);
		Assert.Equal(0, completed.Remaining);
		Assert.Equal(1, completed.FractionSettled);
		_ = Assert.NotNull(completed.StartedAt);
		_ = Assert.NotNull(completed.CompletedAt);
	}

	[Fact]
	public async Task MonitoringLeavesMaxAttemptsUnknownForAnUnregisteredPersistedJob()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var monitor = scope.ServiceProvider.GetRequiredService<IJobMonitor>();
		var now = harness.TimeProvider.GetUtcNow();
		await harness.Storage.EnqueueAsync(
			new()
			{
				JobId = JobHandle.FromString("unknown-definition"),
				JobName = "unknown-definition",
				Payload = "{}",
				State = JobState.Pending,
				CreatedAt = now,
				DueAt = now,
			},
			cancellationToken
		);

		var status = Assert.IsType<JobStatus>(await monitor.GetJobAsync(JobHandle.FromString("unknown-definition"), cancellationToken));
		Assert.Equal(0, status.MaxAttempts);
	}

	[Fact]
	public async Task FailedAttemptDiscardsMidJobBufferAndSuccessfulRetrySplicesOnce()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var workflow = new BatchWorkflowState();
		var expansion = new DynamicExpansionState { FailuresRemaining = 1 };
		await using var harness = CreateHarness(workflow, expansion);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var expanding = scope.ServiceProvider.GetRequiredService<DynamicExpansionJob.Scheduler>();
		var workflowScheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		var current = expanding.Enqueue(new(), batch);
		var waiter = workflowScheduler.ScheduleAfter(
			new("original-waiter"),
			current
		);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(JobState.Scheduled, (await harness.GetJobAsync(current.JobId, cancellationToken)).State);
		Assert.Equal(JobState.AwaitingContinuation, (await harness.GetJobAsync(waiter.JobId, cancellationToken)).State);
		Assert.Equal(2, (await harness.QueryJobsAsync(cancellationToken: cancellationToken)).Count);

		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromSeconds(1), cancellationToken);

		var jobs = (await harness.QueryJobsAsync(cancellationToken: cancellationToken))
			.Where(job => job.BatchId == batchHandle)
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
	public async Task RunningJobCanImmediatelyAddConcurrentWorkToItsBatch()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var workflow = new BatchWorkflowState();
		await using var harness = CreateHarness(workflow);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var expanding = scope.ServiceProvider.GetRequiredService<ConcurrentExpansionJob.Scheduler>();
		var workflowScheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		var current = expanding.Enqueue(new(), batch);
		var waiter = workflowScheduler.ScheduleAfter(
			new("waiter"),
			current
		);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		await harness.DrainAsync(cancellationToken);

		var jobs = (await harness.QueryJobsAsync(cancellationToken: cancellationToken))
			.Where(job => job.BatchId == batchHandle)
			.ToArray();
		Assert.Equal(3, jobs.Length);
		Assert.All(jobs, static job => Assert.Equal(JobState.Succeeded, job.State));
		Assert.Equal(["expanding", "inserted", "waiter"], workflow.Events);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(waiter.JobId, cancellationToken)).State);
	}

	[Fact]
	public async Task ExecutionBufferAcceptsConcurrentAdditionsAndRejectsAdditionsAfterSealing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var probe = new ExecutionBufferProbeState();
		await using var harness = CreateHarness(executionBufferProbe: probe);
		await using var scope = harness.Services.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<ExecutionBufferProbeJob.Scheduler>();
		var workflowScheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		_ = await scheduler.EnqueueAsync(new(64), cancellationToken);
		await harness.DrainAsync(cancellationToken);

		var jobs = await harness.QueryJobsAsync(cancellationToken: cancellationToken);
		var root = Assert.Single(jobs, static job => string.Equals(job.JobName, "execution-buffer-probe", StringComparison.Ordinal));
		Assert.True(root.State == JobState.Succeeded, root.LastError);
		Assert.Equal(65, jobs.Count);
		Assert.Equal(64, jobs.Count(static job => string.Equals(job.JobName, "batch-workflow-test", StringComparison.Ordinal)));
		var details = Assert.IsType<JobDetails>(probe.Details);
		var exception = Assert.Throws<ImmediateJobException>(() =>
			workflowScheduler.ScheduleAfter(new("late"), details, ContinuationOptions.Detached));
		Assert.Contains("sealed", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task RuntimeRegistersScopedBatchAndMonitoringServices()
	{
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var services = new ServiceCollection();
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddImmediateJobsCore();
		await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

		await using var firstScope = provider.CreateAsyncScope();
		var scheduler = firstScope.ServiceProvider.GetRequiredService<BatchScheduler>();
		var batchMonitor = firstScope.ServiceProvider.GetRequiredService<JobMonitor>();
		var jobMonitor = firstScope.ServiceProvider.GetRequiredService<IJobMonitor>();
		await using var secondScope = provider.CreateAsyncScope();

		_ = Assert.IsType<BatchScheduler>(scheduler);
		Assert.Same(batchMonitor, jobMonitor);
		Assert.NotSame(scheduler, secondScope.ServiceProvider.GetRequiredService<BatchScheduler>());
	}

	private static JobTestHarness CreateHarness(
		BatchWorkflowState? state = null,
		DynamicExpansionState? expansion = null,
		ExecutionBufferProbeState? executionBufferProbe = null
	) => new(services =>
	{
		_ = services.AddSingleton(state ?? new());
		_ = services.AddSingleton(expansion ?? new());
		_ = services.AddSingleton(executionBufferProbe ?? new());
		_ = services.AddSingleton(new ExecutionState());
		_ = services.AddSingleton(new ContextProbe());
		_ = services.AddScoped<PropagationScopeState>();
		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
	});

	private static void AssertBefore(IList<string> events, string first, string second)
	{
		var observed = events.ToArray();
		var firstIndex = Array.IndexOf(observed, first);
		var secondIndex = Array.IndexOf(observed, second);
		Assert.True(firstIndex >= 0, $"Expected to observe '{first}', but observed: {string.Join(", ", events)}");
		Assert.True(secondIndex >= 0, $"Expected to observe '{second}', but observed: {string.Join(", ", events)}");
		Assert.True(
			firstIndex < secondIndex,
			$"Expected '{first}' before '{second}', but observed: {string.Join(", ", events)}"
		);
	}
}

public sealed class BatchWorkflowState
{
	public IList<string> Events { get; } = [];
}

public sealed class DynamicExpansionState
{
	public int FailuresRemaining { get; set; }
	public int Attempts { get; set; }
}

public sealed class ExecutionBufferProbeState
{
	public JobDetails? Details { get; set; }
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
			new("inserted"),
			payload.JobDetails ?? throw new InvalidOperationException("Job details were not populated."),
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

[Handler, Job(Name = "concurrent-expansion-test", MaxAttempts = 1)]
public sealed partial class ConcurrentExpansionJob(
	BatchWorkflowJob.Scheduler scheduler,
	BatchWorkflowState? state = null
)
{
	public sealed record Payload : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private async ValueTask HandleAsync(Payload payload, CancellationToken _)
	{
		state?.Events.Add("expanding");
		scheduler.ScheduleAfter(
			new("inserted"),
			payload.JobDetails ?? throw new InvalidOperationException("Job details were not populated."),
			ContinuationOptions.BeforeContinuations
		);
	}
}

[Handler, Job(Name = "execution-buffer-probe", MaxAttempts = 1)]
public sealed partial class ExecutionBufferProbeJob(
	BatchWorkflowJob.Scheduler scheduler,
	ExecutionBufferProbeState? state = null
)
{
	public sealed record Payload(int Count) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		var details = payload.JobDetails
			?? throw new InvalidOperationException("Job details were not populated.");
		var probe = state ?? throw new InvalidOperationException("Execution-buffer probe state was not registered.");
		probe.Details = details;
		_ = Parallel.For(
			0,
			payload.Count,
			index => scheduler.ScheduleAfter(
				new(string.Create(CultureInfo.InvariantCulture, $"buffer-{index}")),
				details,
				ContinuationOptions.Detached
			)
		);
		return ValueTask.CompletedTask;
	}
}

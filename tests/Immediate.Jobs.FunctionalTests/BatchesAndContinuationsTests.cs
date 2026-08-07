using System.Collections.Concurrent;
using System.Globalization;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.FunctionalTests;

#pragma warning disable CS1591
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

		Assert.False(string.IsNullOrWhiteSpace(handle.Id));
		Assert.True(Guid.TryParseExact(handle.Id, "N", out _));
		Assert.Equal(handle.Id, (await harness.GetJobAsync(handle.Id, cancellationToken)).Id);
		Assert.Equal(handle, new JobHandle(handle.Id));
	}

	[Fact]
	public async Task TypedSchedulersCancelJobsAndCommittedBatches()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var jobHandle = await scheduler.EnqueueAsync(new("cancel-job"), cancellationToken);

		await scheduler.CancelAsync(jobHandle, cancellationToken);

		Assert.Equal(JobState.Cancelled, (await harness.GetJobAsync(jobHandle.Id, cancellationToken)).State);

		await using var batch = batches.Begin();
		var memberHandle = scheduler.AddToBatch(batch, new("cancel-batch"));
		var batchHandle = await batch.CommitAsync(cancellationToken);

		await batches.CancelAsync(batchHandle, cancellationToken);

		Assert.Equal(JobState.Cancelled, (await harness.GetJobAsync(memberHandle.Id, cancellationToken)).State);
		var graphStorage = Assert.IsAssignableFrom<IJobGraphStorage>(harness.Storage);
		var status = Assert.IsType<BatchStatus>(await graphStorage.GetBatchStatusAsync(batchHandle.Id, cancellationToken));
		Assert.Equal(BatchState.Cancelled, status.State);
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
	public async Task ConcurrentBatchAdditionsAreSnapshottedExactlyOnce()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		var handles = new ConcurrentBag<JobHandle>();

		_ = Parallel.For(0, 128, index => handles.Add(scheduler.AddToBatch(batch, new(string.Create(CultureInfo.InvariantCulture, $"job-{index}")))));
		var batchHandle = await batch.CommitAsync(cancellationToken);

		var jobs = (await harness.QueryJobsAsync(cancellationToken: cancellationToken))
			.Where(job => string.Equals(job.BatchId, batchHandle.Id, StringComparison.Ordinal))
			.ToArray();
		Assert.Equal(128, handles.Count);
		Assert.Equal(128, jobs.Length);
		Assert.Equal(128, jobs.Select(static job => job.Id).Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task CommitSealsImmutableSnapshotsBeforeAwaitingStorage()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var inner = new InMemoryJobStorage(TimeProvider.System);
		await using var proxy = Storage.SingleServerJobStorageTests.CreateProxy(inner);
		var proxyState = (Storage.SingleServerJobStorageTests.DurableStorageProxy)(object)proxy;
		proxyState.BlockBatchEnqueue = true;
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IJobStorage>(proxy);
		_ = services.AddSingleton(new BatchWorkflowState());
		_ = services.AddSingleton(new DynamicExpansionState());
		_ = services.AddSingleton(new ExecutionBufferProbeState());
		_ = services.AddImmediateJobsCore();
		_ = services.AddImmediateJobsFunctionalTestsHandlers();
		_ = services.AddImmediateJobsFunctionalTestsJobs();
		await using var provider = services.BuildServiceProvider();
		var batches = provider.GetRequiredService<IJobBatchScheduler>();
		var scheduler = provider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		_ = scheduler.AddToBatch(batch, new("seed"));

		var commit = batch.CommitAsync(cancellationToken).AsTask();
		await proxyState.BatchEnqueueEntered.Task.WaitAsync(cancellationToken);
		_ = Assert.Throws<ImmediateJobException>(() => scheduler.AddToBatch(batch, new("too-late")));
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => batch.CommitAsync(cancellationToken).AsTask());
		await batch.DisposeAsync();

		var capturedJobs = Assert.IsType<IReadOnlyList<JobRecord>>(proxyState.CapturedBatchJobs, exactMatch: false);
		var capturedEdges = Assert.IsType<IReadOnlyList<JobContinuationEdge>>(proxyState.CapturedBatchEdges, exactMatch: false);
		_ = Assert.Single(capturedJobs);
		Assert.Empty(capturedEdges);
		_ = Assert.Throws<NotSupportedException>(() =>
			((IList<JobRecord>)capturedJobs).Add(capturedJobs[0]));
		_ = proxyState.BatchEnqueueRelease.TrySetResult();
		_ = await commit.WaitAsync(cancellationToken);
		_ = Assert.Single(capturedJobs);
	}

	[Fact]
	public async Task GroupedBatchAdditionsUseTheSameNormalizationAsDirectScheduling()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();

		_ = scheduler.AddToBatchInGroup(batch, new("grouped"), groupId: "tenant-a");
		_ = scheduler.AddToBatchInGroup(batch, new("blank"), groupId: "  ");
		_ = scheduler.AddToBatchAt(batch, new("absolute"), harness.TimeProvider.GetUtcNow(), groupId: "tenant-b");
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
	public async Task FollowUpBatchAddsParentBatchDependencyOnlyToGraphRoots()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var monitor = scope.ServiceProvider.GetRequiredService<IJobBatchMonitor>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();

		await using var parentBatch = batches.Begin();
		_ = scheduler.AddToBatch(parentBatch, new("parent"));
		var parentBatchHandle = await parentBatch.CommitAsync(cancellationToken);

		await using var followUpBatch = batches.Begin(parentBatchHandle, ContinuationTrigger.Complete);
		var root = scheduler.AddToBatch(followUpBatch, new("root"));
		var child = await scheduler.ScheduleAfterAsync(root, new("child"), cancellationToken: cancellationToken);
		var followUpHandle = await followUpBatch.CommitAsync(cancellationToken);

		var graph = Assert.IsType<BatchGraph>(await monitor.GetGraphAsync(followUpHandle.Id, cancellationToken));
		Assert.Equal(2, graph.Edges.Count);
		var childEdge = Assert.Single(graph.Edges, edge => string.Equals(edge.ChildJobId, child.Id, StringComparison.Ordinal));
		Assert.Equal(root.Id, childEdge.ParentJobId);
		Assert.Null(childEdge.ParentBatchId);
		var rootEdge = Assert.Single(graph.Edges, edge => string.Equals(edge.ChildJobId, root.Id, StringComparison.Ordinal));
		Assert.Null(rootEdge.ParentJobId);
		Assert.Equal(parentBatchHandle.Id, rootEdge.ParentBatchId);
		Assert.Equal(ContinuationTrigger.Complete, rootEdge.Trigger);
		Assert.Equal(1, (await harness.GetJobAsync(root, cancellationToken)).RemainingDependencies);
		Assert.Equal(1, (await harness.GetJobAsync(child, cancellationToken)).RemainingDependencies);
	}

	[Fact]
	public async Task SuccessfulBatchSkipsFailureBranchAndStillSucceeds()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var state = new BatchWorkflowState();
		await using var harness = CreateHarness(state);
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var monitor = scope.ServiceProvider.GetRequiredService<IJobBatchMonitor>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();

		var parent = scheduler.AddToBatch(batch, new("parent"));
		var failureOnly = await scheduler.ScheduleAfterAsync(
			parent,
			new("failure-only"),
			ContinuationTrigger.Failure,
			cancellationToken: cancellationToken
		);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		await harness.DrainAsync(cancellationToken);

		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(parent.Id, cancellationToken)).State);
		Assert.Equal(JobState.Skipped, (await harness.GetJobAsync(failureOnly.Id, cancellationToken)).State);
		var status = Assert.IsType<BatchStatus>(await monitor.GetStatusAsync(batchHandle.Id, cancellationToken));
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
		Assert.Equal(JobState.Skipped, (await harness.GetJobAsync(successOnly.Id, cancellationToken)).State);
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

		var graphStorage = Assert.IsType<IJobGraphStorage>(harness.Storage, exactMatch: false);
		Assert.Equal(
			BatchState.Failed,
			(await graphStorage.GetBatchStatusAsync(batchHandle.Id, cancellationToken))!.State
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

		var child = await scheduler.ScheduleAfterAsync(parent, new("child"), cancellationToken: cancellationToken);

		Assert.Equal(JobState.Pending, (await harness.GetJobAsync(child.Id, cancellationToken)).State);
		await harness.DrainAsync(cancellationToken);
		Assert.Equal(["parent", "child"], state.Events);
	}

	[Fact]
	public async Task StandaloneContinuationsRejectInvalidParentsBeforePersistence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var harness = CreateHarness();
		await using var scope = harness.Services.CreateAsyncScope();
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var scheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		var standalone = await scheduler.EnqueueAsync(new("standalone"), cancellationToken);
		await using var batch = batches.Begin();
		var batched = scheduler.AddToBatch(batch, new("batched"));
		_ = await batch.CommitAsync(cancellationToken);
		await using var foreignBatch = batches.Begin();
		var foreignBatched = scheduler.AddToBatch(foreignBatch, new("foreign-batched"));
		var expectedCount = (await harness.QueryJobsAsync(cancellationToken: cancellationToken)).Count;

		async Task AssertRejected(string expectedMessage, params JobHandle[] parents)
		{
			var exception = await Assert.ThrowsAsync<ImmediateJobException>(async () =>
				await scheduler.ScheduleAfterAsync(parents, new("invalid"), cancellationToken: cancellationToken));
			Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(expectedCount, (await harness.QueryJobsAsync(cancellationToken: cancellationToken)).Count);
		}

		await AssertRejected("non-empty", new JobHandle());
		await AssertRejected("non-empty", new JobHandle(), standalone);
		await AssertRejected("non-empty", standalone, new JobHandle());
		await AssertRejected("duplicate", standalone, standalone);
		await AssertRejected("unrelated scopes", standalone, batched);
		await AssertRejected("unrelated scopes", batched, standalone);
		await AssertRejected("unrelated scopes", batched, foreignBatched);
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
		Assert.Equal(1, childStatus.MaxAttempts);
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
				Id = "unknown-definition",
				JobName = "unknown-definition",
				Payload = "{}",
				State = JobState.Pending,
				CreatedAt = now,
				DueAt = now,
			},
			cancellationToken
		);

		var status = Assert.IsType<JobStatus>(await monitor.GetJobAsync("unknown-definition", cancellationToken));
		Assert.Null(status.MaxAttempts);
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
			.Where(job => string.Equals(job.BatchId, batchHandle.Id, StringComparison.Ordinal))
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
		var batches = scope.ServiceProvider.GetRequiredService<IJobBatchScheduler>();
		var expanding = scope.ServiceProvider.GetRequiredService<ConcurrentExpansionJob.Scheduler>();
		var workflowScheduler = scope.ServiceProvider.GetRequiredService<BatchWorkflowJob.Scheduler>();
		await using var batch = batches.Begin();
		var current = expanding.AddToBatch(batch, new());
		var waiter = await workflowScheduler.ScheduleAfterAsync(
			current,
			new("waiter"),
			cancellationToken: cancellationToken
		);
		var batchHandle = await batch.CommitAsync(cancellationToken);

		await harness.DrainAsync(cancellationToken);

		var jobs = (await harness.QueryJobsAsync(cancellationToken: cancellationToken))
			.Where(job => string.Equals(job.BatchId, batchHandle.Id, StringComparison.Ordinal))
			.ToArray();
		Assert.Equal(3, jobs.Length);
		Assert.All(jobs, static job => Assert.Equal(JobState.Succeeded, job.State));
		Assert.Equal(["expanding", "inserted", "waiter"], workflow.Events);
		Assert.Equal(JobState.Succeeded, (await harness.GetJobAsync(waiter, cancellationToken)).State);
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
			workflowScheduler.ScheduleAfter(details, new("late"), ContinuationOptions.Detached));
		Assert.Contains("sealed", exception.Message, StringComparison.OrdinalIgnoreCase);
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

	private async ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		state?.Events.Add("expanding");
		_ = await scheduler.AddToBatchAsync(
			payload.JobDetails ?? throw new InvalidOperationException("Job details were not populated."),
			new("inserted"),
			ContinuationOptions.BeforeContinuations,
			cancellationToken
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
			index => scheduler.ScheduleAfter(details, new(string.Create(CultureInfo.InvariantCulture, $"buffer-{index}")), ContinuationOptions.Detached)
		);
		return ValueTask.CompletedTask;
	}
}
#pragma warning restore CS1591

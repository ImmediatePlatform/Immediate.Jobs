using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryJobStorageTests
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Graph;

	public static TheoryData<JobStorageConformanceTestCase> Cases =>
		[.. JobStorageConformanceSuite.GetCases(Capabilities)];

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task InMemoryStorageConforms(JobStorageConformanceTestCase testCase)
	{
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<TimeProvider>(clock);
		services.AddSingleton(clock);
		services.AddImmediateJobsCore().ConfigureStorage(options => _ = options.UseInMemory());

		await using var provider = services.BuildServiceProvider(
			new ServiceProviderOptions
			{
				ValidateOnBuild = true,
				ValidateScopes = true,
			}
		);

		var storage = (InMemoryJobStorage)provider.GetRequiredService<IJobStorage>();

		storage.LoadPersistedJobState(
			testCase.PersistedJobState.Jobs,
			testCase.PersistedJobState.Batches,
			testCase.PersistedJobState.Edges,
			testCase.PersistedJobState.RecurringSchedules
		);

		await testCase.RunAsync(provider, TestContext.Current.CancellationToken);
	}

	[Theory]
	[InlineData("missing-parent-job")]
	[InlineData("missing-parent-batch")]
	[InlineData("missing-child")]
	[InlineData("no-parent")]
	[InlineData("two-parents")]
	[InlineData("duplicate-edge")]
	[InlineData("unknown-trigger")]
	[InlineData("job-cycle")]
	[InlineData("batch-cycle")]
	[InlineData("missing-batch")]
	[InlineData("invalid-batch-counts")]
	[InlineData("missing-edges")]
	[InlineData("duplicate-job")]
	[InlineData("duplicate-batch")]
	[InlineData("duplicate-schedule")]
	public async Task InvalidSnapshotsDoNotPartiallyLoad(string problem)
	{
		await using var storage = new InMemoryJobStorage(TimeProvider.System);
		var existing = Job("existing");
		storage.LoadPersistedJobState([existing], [], [], []);
		var parent = Job("parent");
		var child = Job("child") with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		var first = BatchHandle.FromString("first");
		var second = BatchHandle.FromString("second");
		var edge = new JobContinuationEdge { ParentJobHandle = parent.JobHandle, ChildJobHandle = child.JobHandle, Delay = TimeSpan.Zero };
		List<JobRecord> jobs = [parent, child];
		List<BatchRecord> batches = [];
		List<JobContinuationEdge> edges = [edge];
		List<RecurringJobSchedule> schedules = [];
		switch (problem)
		{
			case "missing-parent-job": edges = [edge with { ParentJobHandle = JobHandle.FromString("missing") }]; break;
			case "missing-parent-batch": edges = [edge with { ParentJobHandle = null, ParentBatchHandle = first }]; break;
			case "missing-child": edges = [edge with { ChildJobHandle = JobHandle.FromString("missing") }]; break;
			case "no-parent": edges = [edge with { ParentJobHandle = null }]; break;
			case "two-parents": edges = [edge with { ParentBatchHandle = first }]; break;
			case "duplicate-edge": edges.Add(edge with { Delay = TimeSpan.FromSeconds(1) }); break;
			case "unknown-trigger": edges = [edge with { Trigger = (ContinuationTrigger)999 }]; break;
			case "job-cycle":
				jobs[0] = parent with { State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
				edges.Add(edge with { ParentJobHandle = child.JobHandle, ChildJobHandle = parent.JobHandle });
				break;
			case "batch-cycle":
				jobs = [parent with { BatchHandle = first, State = JobState.AwaitingContinuation, RemainingDependencies = 1 }, child with { BatchHandle = second }];
				batches = [Batch(first, 1), Batch(second, 1)];
				edges = [edge with { ParentJobHandle = null, ParentBatchHandle = first }, edge with { ParentJobHandle = null, ParentBatchHandle = second, ChildJobHandle = parent.JobHandle }];
				break;
			case "missing-batch": jobs[1] = child with { BatchHandle = first }; break;
			case "invalid-batch-counts":
				jobs = [parent with { BatchHandle = first }, child with { BatchHandle = first }];
				batches = [Batch(first, 1)];
				break;
			case "missing-edges": edges = []; break;
			case "duplicate-job": jobs.Add(parent); break;
			case "duplicate-batch": batches = [Batch(first, 1), Batch(first, 1)]; break;
			case "duplicate-schedule": schedules = [Schedule("repeat", "work"), Schedule("REPEAT", "work")]; break;
			default: throw new ArgumentOutOfRangeException(nameof(problem));
		}

		if (string.Equals(problem, "unknown-trigger", StringComparison.Ordinal))
			Assert.Throws<ArgumentOutOfRangeException>(() => storage.LoadPersistedJobState(jobs, batches, edges, schedules));
		else
			Assert.Throws<ImmediateJobException>(() => storage.LoadPersistedJobState(jobs, batches, edges, schedules));
		Assert.Equal(existing.JobHandle, Assert.Single(await storage.QueryJobsAsync(new(), TestContext.Current.CancellationToken)).JobHandle);
		Assert.Empty(await storage.QueryBatchesAsync(new(), TestContext.Current.CancellationToken));
		Assert.Empty((await storage.GetMonitoringDefinitionsAsync(TestContext.Current.CancellationToken)).Recurring);
	}

	[Fact]
	public async Task BulkLoadAcceptsUnorderedBatchDependenciesAndPreservesProgress()
	{
		await using var storage = new InMemoryJobStorage(TimeProvider.System);
		var parentBatch = BatchHandle.FromString("parent");
		var childBatch = BatchHandle.FromString("child");
		var parent = Job("parent") with { BatchHandle = parentBatch, State = JobState.Active, WorkerId = "worker", LeaseExpiresAt = DateTimeOffset.MaxValue, Attempt = 3 };
		var child = Job("child") with { BatchHandle = childBatch, State = JobState.WaitingForTrigger, RemainingDependencies = 1 };
		var edge = new JobContinuationEdge { ParentBatchHandle = parentBatch, ChildJobHandle = child.JobHandle, Delay = TimeSpan.FromHours(2) };
		storage.LoadPersistedJobState([child, parent], [Batch(childBatch, 1) with { State = BatchState.WaitingForTrigger }, Batch(parentBatch, 1)], [edge], []);

		var recovered = await storage.QueryJobsAsync(new(), TestContext.Current.CancellationToken);
		Assert.Contains(parent, recovered);
		Assert.Contains(child, recovered);
		Assert.Equal(edge, Assert.Single((await storage.GetBatchGraphAsync(childBatch, TestContext.Current.CancellationToken))!.Edges));
	}

	[Fact]
	public async Task RecoveryDoesNotSettleTerminalParentsTwice()
	{
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(10));
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = "work" }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, TestContext.Current.CancellationToken);
		var done = Job("done") with { State = JobState.Failed, CompletedAt = clock.GetUtcNow().AddHours(-1) };
		var pending = Job("pending");
		var child = Job("child") with { State = JobState.AwaitingContinuation, RemainingDependencies = 1, FailedDependencies = 1, DueAt = clock.GetUtcNow().AddHours(3) };
		var settled = new JobContinuationEdge { ParentJobHandle = done.JobHandle, ChildJobHandle = child.JobHandle, Trigger = ContinuationTrigger.Complete, Delay = TimeSpan.FromHours(2) };
		storage.LoadPersistedJobState([child, pending, done], [], [settled, settled with { ParentJobHandle = pending.JobHandle }], []);
		await storage.EnqueueContinuationAsync(new()
		{
			JobHandle = JobHandle.FromString("new-child"),
			JobName = "work",
			Payload = "{}",
			State = JobState.AwaitingContinuation,
			CreatedAt = clock.GetUtcNow(),
			DueAt = clock.GetUtcNow(),
			RemainingDependencies = 1,
		}, [settled with { ChildJobHandle = JobHandle.FromString("new-child") }], TestContext.Current.CancellationToken);

		var recovered = Assert.Single(await storage.QueryJobsAsync(new() { JobHandle = child.JobHandle }, TestContext.Current.CancellationToken));
		Assert.Equal(child.State, recovered.State);
		Assert.Equal(child.RemainingDependencies, recovered.RemainingDependencies);
		Assert.Equal(child.FailedDependencies, recovered.FailedDependencies);
		Assert.Equal(child.DueAt, recovered.DueAt);
	}

	[Fact]
	public async Task RecoveryRestoresRecurringOccurrenceDeduplication()
	{
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var existing = Job("existing-occurrence") with { RecurringKey = "occurrence-key" };
		var schedule = Schedule("recurring", "work");
		storage.LoadPersistedJobState([existing], [], [], [schedule]);
		var inserted = await storage.MaterializeRecurringAsync(schedule, existing with { JobHandle = JobHandle.FromString("duplicate-occurrence") },
			clock.GetUtcNow().AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(inserted);
		Assert.Equal(existing.JobHandle, Assert.Single(await storage.QueryJobsAsync(new(), TestContext.Current.CancellationToken)).JobHandle);
	}

	[Fact]
	public async Task SingleServerMergeReconcilesSchedulesWithoutReloadingMonitoringSnapshots()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(10));
		await using var durable = new SnapshotCountingStorage(clock);
		var now = clock.GetUtcNow();
		var original = Schedule("code", "code") with { IsPaused = true, LastRunAt = now.AddMinutes(-2), NextRunAt = now.AddMinutes(-1) };
		var dynamic = Schedule("dynamic", "ordinary") with { IsCodeDefined = false, NextRunAt = now };
		durable.LoadPersistedJobState([], [], [], [original, dynamic, Schedule("obsolete", "obsolete")]);
		JobDefinitionRecord code = new() { Name = "code", Cron = original.Cron };
		JobDefinitionRecord ordinary = new() { Name = "ordinary" };
		await durable.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [code, ordinary, new() { Name = "obsolete", Cron = original.Cron }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { ["code"] = original.NextRunAt, ["obsolete"] = now },
		}, token);
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<TimeProvider>(clock);
		services.AddImmediateJobsCore().ConfigureStorage(builder => builder.UseSingleServer(_ => durable));
		await using var provider = services.BuildServiceProvider();
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(token);
		Assert.Equal(1, durable.SnapshotReads);
		Assert.Equal(1, durable.DefinitionReads);
		Assert.Equal("dynamic", Assert.Single(await writer.GetDueRecurringAsync(now, 10, ["code", "ordinary"], token)).Name);

		code = code with { Cron = "15 * * * *" };
		JobDefinitionRecord added = new() { Name = "added", Cron = original.Cron };
		var registration = new JobDefinitionRegistration
		{
			Definitions = [code, ordinary, added],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase) { ["code"] = now.AddHours(1), ["added"] = now },
		};
		await writer.MergeJobDefinitionsListAsync(registration, token);
		Assert.Equal(["added", "dynamic"], (await writer.GetDueRecurringAsync(now, 10, ["code", "ordinary", "obsolete", "added"], token)).Select(static schedule => schedule.Name).Order(StringComparer.Ordinal));
		await writer.ResumeRecurringAsync("code", token);
		var due = await writer.GetDueRecurringAsync(now.AddHours(2), 10, ["code", "ordinary", "obsolete", "added"], token);
		var updated = Assert.Single(due, static schedule => schedule.IsCodeDefined && string.Equals(schedule.Name, "code", StringComparison.Ordinal));
		Assert.Equal(original.LastRunAt, updated.LastRunAt);
		Assert.Equal(now.AddHours(1), updated.NextRunAt);
		Assert.Equal(code.Cron, updated.Cron);
		Assert.Equal(dynamic, Assert.Single(due, static schedule => !schedule.IsCodeDefined));
		await writer.MergeJobDefinitionsListAsync(registration with { Definitions = [code, added] }, token);
		Assert.DoesNotContain(await writer.GetDueRecurringAsync(now.AddHours(2), 10, ["ordinary"], token), static schedule => !schedule.IsCodeDefined);
		Assert.Equal(1, durable.SnapshotReads);
		Assert.Equal(1, durable.DefinitionReads);
	}

	[Fact]
	public async Task CompletionUpdatesTheStoredParentAfterInsertingManyContinuations()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var parent = Job("parent", "Work") with { State = JobState.Active, Attempt = 1, WorkerId = "worker", LeaseExpiresAt = clock.GetUtcNow().AddHours(1) };
		storage.LoadPersistedJobState([parent], [], [], []);
		var additions = Enumerable.Range(0, 512).Select(i => new JobContinuationAddition
		{
			Job = Submission(JobHandle.FromString(string.Create(CultureInfo.InvariantCulture, $"child-{i}")), "Work"),
			Options = ContinuationOptions.Detached,
			Delay = TimeSpan.Zero,
		}).ToList();

		await storage.CompleteWithContinuationsAsync(parent.JobHandle, 1, "worker", additions, token);

		Assert.Equal(JobState.Succeeded, Assert.Single(await storage.QueryJobsAsync(new() { JobHandle = parent.JobHandle }, token)).State);
		var children = await storage.QueryNonCompletedJobsAsync("work", token);
		Assert.Equal(512, children.Count);
		Assert.All(children, static child => Assert.Equal(JobState.Pending, child.State));
	}

	[Fact]
	public async Task RejectedBatchDoesNotReserveItsHandleOrItsJobs()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider());
		await RegisterAsync(storage, token);
		var batch = Batch(BatchHandle.FromString("batch"), 1);
		var job = Submission(JobHandle.FromString("job"), "Work") with { BatchHandle = batch.BatchHandle };
		JobContinuationEdge edge = new() { ChildJobHandle = job.JobHandle, ParentJobHandle = JobHandle.FromString("missing"), Trigger = ContinuationTrigger.Success, Delay = TimeSpan.Zero };

		await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.EnqueueBatchAsync(batch, [job], [edge], token));
		Assert.Null(await storage.GetBatchStatusAsync(batch.BatchHandle, token));
		Assert.Empty(await storage.QueryNonCompletedJobsAsync("work", token));
		await storage.EnqueueBatchAsync(batch, [job], [], token);
		Assert.Single(await storage.QueryBatchMembersAsync(batch.BatchHandle, new(), token));
	}

	[Fact]
	public async Task BatchTriggerDoesNotRetriggerMembersSkippedDuringPropagation()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider());
		await RegisterAsync(storage, token);
		var batch = BatchHandle.FromString("batch");
		var failed = Job("failed", "Other") with { State = JobState.Failed, CompletedAt = DateTimeOffset.UnixEpoch };
		var first = Job("first", "Work") with { BatchHandle = batch, State = JobState.WaitingForTrigger };
		var second = Job("second", "Work") with { BatchHandle = batch, State = JobState.WaitingForTrigger, RemainingDependencies = 1 };
		storage.LoadPersistedJobState([failed, first, second], [Batch(batch, 2) with { State = BatchState.WaitingForTrigger }],
		[
			new() { ChildJobHandle = first.JobHandle, ParentJobHandle = failed.JobHandle, Trigger = ContinuationTrigger.Success, Delay = TimeSpan.Zero },
			new() { ChildJobHandle = second.JobHandle, ParentJobHandle = first.JobHandle, Trigger = ContinuationTrigger.Success, Delay = TimeSpan.Zero },
		], []);

		Assert.True(await storage.TryTriggerBatchAsync(batch, token));
		Assert.All(await storage.QueryBatchMembersAsync(batch, new(), token), static member => Assert.Equal(JobState.Skipped, member.State));
		var status = await storage.GetBatchStatusAsync(batch, token);
		Assert.NotNull(status);
		Assert.Equal(BatchState.Succeeded, status.State);
		Assert.Equal(2, status.Skipped);
		Assert.False(await storage.TryTriggerBatchAsync(batch, token));
	}

	[Fact]
	public async Task BatchAndDefinitionLookupsReflectRecoveryCancellationPurgeAndHandleReuse()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var batch = BatchHandle.FromString("batch");
		var otherBatch = BatchHandle.FromString("other-batch");
		var active = Job("active", "WORK") with { BatchHandle = batch, State = JobState.Active, WorkerId = "worker", LeaseExpiresAt = clock.GetUtcNow().AddHours(1) };
		var pending = Job("pending", "Work") with { BatchHandle = batch };
		var other = Job("other", "Other") with { BatchHandle = otherBatch };
		storage.LoadPersistedJobState([active, pending, other], [Batch(batch, 2), Batch(otherBatch, 1)], [], []);
		Assert.Equal(1, (await storage.GetJobAcquisitionStateAsync("work", token)).ActiveCount);
		Assert.Equal(2, (await storage.QueryJobsAsync(new() { JobName = "wOrK" }, token)).Count);
		Assert.Equal(2, (await storage.QueryBatchMembersAsync(batch, new(), token)).Count);
		Assert.Empty(await storage.QueryJobsAsync(new() { JobHandle = other.JobHandle, JobName = "Work" }, token));

		await storage.CancelBatchAsync(batch, token);
		Assert.Equal(2, (await storage.QueryBatchMembersAsync(batch, new() { State = JobState.Cancelled }, token)).Count);
		Assert.All((await storage.GetBatchGraphAsync(batch, token))!.Nodes, static node => Assert.Equal(JobState.Cancelled, node.State));
		Assert.Empty(await storage.QueryNonCompletedJobsAsync("WORK", token));
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = "Other" }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		Assert.Equal(2, (await storage.QueryJobsAsync(new() { JobName = "work" }, token)).Count);
		clock.Advance(TimeSpan.FromTicks(1));
		await storage.PurgeBatchesAsync(TimeSpan.Zero, TimeSpan.Zero, token);
		Assert.Empty(await storage.QueryJobsAsync(new() { JobName = "work" }, token));
		Assert.Empty(await storage.QueryBatchMembersAsync(batch, new(), token));
		Assert.Single(await storage.QueryNonCompletedJobsAsync("other", token));

		await RegisterAsync(storage, token);
		await storage.EnqueueBatchAsync(Batch(batch, 1), [Submission(active.JobHandle, "work") with { BatchHandle = batch }], [], token);
		Assert.Single(await storage.QueryBatchMembersAsync(batch, new(), token));
		Assert.Single(await storage.QueryNonCompletedJobsAsync("WORK", token));
		Assert.Single((await storage.GetBatchGraphAsync(batch, token))!.Nodes);
	}

	[Fact]
	public async Task StandalonePurgeRemovesDefinitionMembershipBeforeHandleReuse()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var handle = JobHandle.FromString("reused");
		await storage.EnqueueAsync(Submission(handle, "work"), token);
		await storage.EnqueueAsync(Submission(JobHandle.FromString("other"), "other"), token);
		await storage.CancelAsync(handle, token);
		clock.Advance(TimeSpan.FromTicks(1));
		await storage.PurgeJobsAsync(TimeSpan.Zero, TimeSpan.Zero, token);
		Assert.Empty(await storage.QueryJobsAsync(new() { JobName = "Work" }, token));
		await storage.EnqueueAsync(Submission(handle, "WORK"), token);
		Assert.Single(await storage.QueryNonCompletedJobsAsync("work", token));
		await storage.CancelAsync(handle, token);
		clock.Advance(TimeSpan.FromTicks(1));
		await storage.PurgeJobsAsync(TimeSpan.Zero, TimeSpan.Zero, token);
		Assert.Empty(await storage.QueryJobsAsync(new() { JobName = "work" }, token));
		Assert.Single(await storage.QueryNonCompletedJobsAsync("other", token));
		await storage.EnqueueAsync(Submission(handle, "work"), token);
		Assert.Single(await storage.QueryJobsAsync(new() { JobName = "WORK" }, token));
	}

	[Fact]
	public async Task RecurringMaterializationIndexesNewJobsAndDedupeDoesNotAddPhantomHandles()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var original = Job("existing", "Work");
		var schedule = new RecurringJobSchedule
		{
			Name = "dynamic",
			JobName = "Other",
			Cron = "* * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = clock.GetUtcNow(),
		};
		storage.LoadPersistedJobState([original], [], [], [schedule]);
		var materialized = Job("recurring", "Other") with { RecurringKey = "occurrence" };
		var next = clock.GetUtcNow().AddMinutes(1);
		Assert.True(await storage.MaterializeRecurringAsync(schedule, materialized, next, cancellationToken: token));
		Assert.Equal(original.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { JobName = "work" }, token)).JobHandle);
		Assert.Equal(materialized.JobHandle, Assert.Single(await storage.QueryNonCompletedJobsAsync("OTHER", token)).JobHandle);
		Assert.False(await storage.MaterializeRecurringAsync(schedule with { NextRunAt = next }, materialized with { JobHandle = JobHandle.FromString("duplicate") }, next.AddMinutes(1), cancellationToken: token));
		Assert.Single(await storage.QueryJobsAsync(new() { JobName = "other" }, token));
		Assert.Empty(await storage.QueryJobsAsync(new() { JobHandle = JobHandle.FromString("duplicate") }, token));
	}

	[Fact]
	public async Task QueueQueriesFollowCurrentDefinitionsAndComposeWithOtherFilters()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider());
		await RegisterAsync(storage, [new() { Name = "A", QueueName = "q" }, new() { Name = "B", QueueName = "Q" }], token);
		var a = Job("a", "a");
		var b = Job("b", "B");
		var orphan = Job("orphan", "removed") with { State = JobState.Succeeded };
		storage.LoadPersistedJobState([a, b, orphan], [], [], []);

		Assert.Equal(a.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { QueueName = "q" }, token)).JobHandle);
		Assert.Equal(b.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { QueueName = "Q", State = JobState.Pending }, token)).JobHandle);
		Assert.Empty(await storage.QueryJobsAsync(new() { QueueName = "q", JobName = "B" }, token));
		Assert.Empty(await storage.QueryJobsAsync(new() { QueueName = "Q", JobHandle = a.JobHandle }, token));
		Assert.Empty(await storage.QueryJobsAsync(new() { QueueName = "missing" }, token));

		await RegisterAsync(storage, [new() { Name = "A", QueueName = "moved" }, new() { Name = "B", QueueName = "moved" }], token);
		Assert.Equal(2, (await storage.QueryJobsAsync(new() { QueueName = "moved" }, token)).Count);
		Assert.Equal(a.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { QueueName = "moved", JobName = "A" }, token)).JobHandle);
		Assert.Empty(await storage.QueryJobsAsync(new() { QueueName = "q" }, token));

		await RegisterAsync(storage, [new() { Name = "B", QueueName = "moved" }], token);
		Assert.Equal(b.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { QueueName = "moved" }, token)).JobHandle);
		Assert.Equal(3, (await storage.QueryJobsAsync(new(), token)).Count);
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task AcquisitionOnlyClaimsRequestedDefinitionsInTheExactQueue(bool fair, bool grouped)
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider());
		await RegisterAsync(storage,
		[
			new() { Name = "wanted", QueueName = "q" },
			new() { Name = "unrequested", QueueName = "q" },
			new() { Name = "other", QueueName = "Q" },
			new() { Name = "empty-a", QueueName = "q" },
			new() { Name = "empty-b", QueueName = "q" },
		], token);
		var wanted = Job("wanted", "wanted") with { GroupId = grouped ? "group" : null };
		storage.LoadPersistedJobState([Job("unrequested", "unrequested"), Job("other", "other"), wanted], [], [], []);
		var request = Request(fair) with
		{
			Queues = [new()
			{
				QueueName = "q",
				Capacity = 10,
				JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
				{
					["WANTED"] = 1, ["other"] = 10, ["empty-a"] = 1, ["empty-b"] = 1,
				},
			}],
		};

		Assert.Equal(wanted.JobHandle, Assert.Single(await storage.AcquireDueJobsAsync(request, token)).JobHandle);
		Assert.Equal(2, (await storage.QueryJobsAsync(new() { State = JobState.Pending }, token)).Count);
	}

	[Fact]
	public async Task FairUsageIncludesUnrequestedDefinitionsButExcludesOtherQueues()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider());
		await RegisterAsync(storage,
		[
			new() { Name = "wanted", QueueName = "q" },
			new() { Name = "unrequested", QueueName = "q" },
			new() { Name = "other", QueueName = "Q" },
		], token);
		var first = Job("first", "wanted") with { GroupId = "a" };
		var second = Job("second", "wanted") with { GroupId = "b", CreatedAt = DateTimeOffset.UnixEpoch.AddTicks(1) };
		storage.LoadPersistedJobState(
		[
			Active("a-1", "unrequested", "a"), Active("a-2", "unrequested", "a"),
			Active("b-1", "other", "b"), Active("b-2", "other", "b"), Active("b-3", "other", "b"),
			first, second,
		], [], [], []);

		Assert.Equal(second.JobHandle, Assert.Single(await storage.AcquireDueJobsAsync(Request(fair: true), token)).JobHandle);
	}

	[Theory]
	[InlineData(1, 0, 0, 10)]
	[InlineData(0, 1, 0, 10)]
	[InlineData(0, 0, 1, 10)]
	[InlineData(0, 0, 0, 1)]
	public async Task FairAcquisitionAdvancesPastExhaustedDefinitionsWithinEachGroup(
		int maxConcurrency, int slidingMax, int fixedMax, int capacity
	)
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new()
		{
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
			Definitions =
			[
				new()
				{
					Name = "limited",
					MaxConcurrency = maxConcurrency,
					SlidingWindowMax = slidingMax,
					SlidingWindowPeriod = slidingMax > 0 ? TimeSpan.FromMinutes(1) : null,
					FixedWindowMax = fixedMax,
					FixedWindowPeriod = fixedMax > 0 ? TimeSpan.FromMinutes(1) : null,
				},
				new() { Name = "free" },
			],
		}, token);
		(string Id, string Name, string? Group)[] jobs =
		[
			("limited-a", "limited", "a"),
			("limited-a-next", "limited", "a"),
			("free-a", "free", "a"),
			("limited-b", "limited", "b"),
			("free-b", "free", "b"),
			("limited-ungrouped", "limited", null),
			("free-ungrouped", "free", null),
		];
		for (var index = 0; index < jobs.Length; index++)
		{
			var (id, name, group) = jobs[index];
			await storage.EnqueueAsync(Job(id, name, group, clock.GetUtcNow().AddTicks(index)), token);
		}

		var acquired = await storage.AcquireDueJobsAsync(Request(capacity, roundRobin: true), token);

		Assert.Equal(["limited-a", "free-b", "free-ungrouped", "free-a"], acquired.Select(static job => job.JobHandle.Value));
	}

	[Fact]
	public async Task FairAcquisitionReclassifiesNoisyGroupsAfterEachClaim()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new() { RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase), Definitions = [new() { Name = "free" }] }, token);
		for (var index = 0; index < 6; index++)
		{
			var group = index < 3 ? "a" : "b";
			var id = string.Create(CultureInfo.InvariantCulture, $"{group}-{index % 3}");
			await storage.EnqueueAsync(Job(id, "free", group, clock.GetUtcNow().AddTicks(index)), token);
		}

		var acquired = await storage.AcquireDueJobsAsync(Request(10, roundRobin: false), token);

		Assert.Equal(["a-0", "b-0", "a-1", "b-1", "a-2", "b-2"], acquired.Select(static job => job.JobHandle.Value));
	}

	[Fact]
	public async Task ExactJobQueryComposesFiltersAndUsesCurrentQueue()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new() { RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase), Definitions = [new() { Name = "free", QueueName = "old" }] }, token);
		await storage.EnqueueAsync(Job("target", "free", group: null, clock.GetUtcNow()), token);
		await storage.MergeJobDefinitionsListAsync(new() { RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase), Definitions = [new() { Name = "free", QueueName = "current" }] }, token);
		var query = new JobQuery
		{
			JobHandle = JobHandle.FromString("target"),
			State = JobState.Pending,
			JobName = "FREE",
			Search = "REE",
			QueueName = "current",
			CreatedBefore = clock.GetUtcNow().AddTicks(1),
		};

		Assert.Equal("current", Assert.Single(await storage.QueryJobsAsync(query, token)).QueueName);
		Assert.Equal("current", Assert.Single(await storage.QueryNonCompletedJobsAsync("FREE", token)).QueueName);
		JobQuery[] excluded =
		[
			query with { JobHandle = JobHandle.FromString("missing") },
			query with { State = JobState.Scheduled },
			query with { JobName = "other" },
			query with { Search = "other" },
			query with { QueueName = "old" },
			query with { CreatedBefore = clock.GetUtcNow() },
			query with { Skip = 1 },
		];
		foreach (var filter in excluded)
			Assert.Empty(await storage.QueryJobsAsync(filter, token));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LegacyExecutionSurvivesTelemetryFailureAndRetry(bool recordLegacyTelemetry)
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		var now = clock.GetUtcNow();
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = "Work" }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		var handle = JobHandle.FromString("legacy");
		storage.LoadPersistedJobState([new()
		{
			JobHandle = handle,
			JobName = "Work",
			Payload = "{}",
			State = JobState.Active,
			CreatedAt = now,
			DueAt = now,
			Attempt = 3,
			WorkerId = "worker",
			LeaseExpiresAt = now.AddMinutes(1),
		}], [], [], []);
		var original = Assert.Single(await storage.QueryJobExecutionsAsync(handle, new(), token));
		if (recordLegacyTelemetry)
			await storage.SetExecutionTelemetryAsync(handle, 3, "worker", "legacy-trace", "legacy-span", now, token);

		clock.Advance(TimeSpan.FromSeconds(1));
		await storage.FailAsync(handle, 3, "worker", "legacy failure", nextRetryAt: null, token);
		var failed = Assert.Single(await storage.QueryJobExecutionsAsync(handle, new(), token));
		Assert.True(failed.IsSynthetic);
		Assert.Equal(JobExecutionState.Failed, failed.State);
		Assert.Equal("legacy failure", failed.Error);
		Assert.Equal(clock.GetUtcNow(), failed.CompletedAt);
		Assert.Equal(recordLegacyTelemetry ? "legacy-trace" : null, failed.ExecutionTraceId);
		Assert.Equal(recordLegacyTelemetry ? "legacy-span" : null, failed.ExecutionSpanId);
		Assert.Null(failed.AcquiredAt);
		Assert.Equal(JobExecutionState.Active, original.State);
		Assert.Null(original.ExecutionTraceId);

		await storage.RetryAsync(handle, token);
		var active = Assert.Single(await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "worker",
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = 1,
			Queues = [new() { QueueName = JobQueueDefinition.DefaultName, Capacity = 1, JobCapacities = new Dictionary<string, int> { ["Work"] = 1 } }],
		}, token));
		Assert.Equal(4, active.Attempt);
		await storage.SetExecutionTelemetryAsync(handle, 4, "worker", "new-trace", "new-span", clock.GetUtcNow(), token);
		clock.Advance(TimeSpan.FromSeconds(1));
		await storage.CompleteAsync(handle, 4, "worker", token);

		var history = await storage.QueryJobExecutionsAsync(handle, new(), token);
		Assert.Equal([4, 3], history.Select(static execution => execution.Attempt));
		Assert.False(history[0].IsSynthetic);
		Assert.Equal(JobExecutionState.Succeeded, history[0].State);
		Assert.Equal("new-trace", history[0].ExecutionTraceId);
		Assert.Equal("new-span", history[0].ExecutionSpanId);
		Assert.Equal(now.AddSeconds(1), history[0].AcquiredAt);
		Assert.Equal(clock.GetUtcNow(), history[0].CompletedAt);
		Assert.Equal(failed, history[1]);
		Assert.Equal(failed, Assert.Single(await storage.QueryJobExecutionsAsync(handle, new() { Skip = 1, Take = 1 }, token)));
		Assert.Equal(history[0], Assert.Single(await storage.QueryJobExecutionsAsync(handle, new() { Attempt = 4 }, token)));
	}

	private static BatchRecord Batch(BatchHandle handle, int count) => new()
	{
		BatchHandle = handle,
		TotalJobs = count,
		PendingCount = count,
		State = BatchState.Executing,
		CreatedAt = DateTimeOffset.UnixEpoch,
	};

	private static RecurringJobSchedule Schedule(string name, string jobName) => new()
	{
		Name = name,
		JobName = jobName,
		Cron = "* * * * *",
		TimeZone = "UTC",
		IsCodeDefined = true,
		NextRunAt = DateTimeOffset.UnixEpoch,
	};

	private static ValueTask RegisterAsync(InMemoryJobStorage storage, CancellationToken token) => storage.MergeJobDefinitionsListAsync(new()
	{
		Definitions = [new() { Name = "Work", MaxConcurrency = 1 }, new() { Name = "Other" }],
		RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
	}, token);

	[Fact]
	public async Task RenewedLeasesIgnoreOldDeadlinesAndExpireWithoutRequestedQueues()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, [new() { Name = "wanted", QueueName = "q", MaxConcurrency = 1 }], token);
		await storage.EnqueueAsync(Submission(JobHandle.FromString("leased"), "wanted"), token);
		var claimed = Assert.Single(await storage.AcquireDueJobsAsync(Request(fair: false), token));
		clock.Advance(TimeSpan.FromSeconds(30));
		await storage.RenewLeaseAsync(claimed.JobHandle, claimed.Attempt, "worker", TimeSpan.FromMinutes(2), token);
		clock.Advance(TimeSpan.FromSeconds(31));
		Assert.Empty(await storage.AcquireDueJobsAsync(Request(fair: false) with { Queues = [] }, token));
		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(claimed.JobHandle, token))!.State);
		Assert.Equal(1, (await storage.GetJobAcquisitionStateAsync("wanted", token)).ActiveCount);

		clock.Advance(TimeSpan.FromMinutes(2));
		Assert.Equal(0, (await storage.GetJobAcquisitionStateAsync("wanted", token)).ActiveCount);
		Assert.Empty(await storage.AcquireDueJobsAsync(Request(fair: false) with { Queues = [] }, token));
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(claimed.JobHandle, token))!.State);
		var reclaimed = Assert.Single(await storage.AcquireDueJobsAsync(Request(fair: false), token));
		Assert.Equal(claimed.Attempt + 1, reclaimed.Attempt);
		Assert.Equal(JobExecutionState.Interrupted, (await storage.QueryJobExecutionsAsync(claimed.JobHandle, new(), token)).Single(e => e.Attempt == claimed.Attempt).State);
	}

	[Fact]
	public async Task RunnableOrderingHonorsLimitsAliasesAndQueueReassignment()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		JobDefinitionRecord[] definitions =
		[
			new() { Name = "wanted", QueueName = "q", MaxConcurrency = 1 },
			new() { Name = "second", QueueName = "q" },
			new() { Name = "paused", QueueName = "q" },
		];
		await RegisterAsync(storage, definitions, token);
		storage.LoadPersistedJobState(
		[
			Job("z-tie", "second"), Job("a-tie", "wanted"), Job("b-exceeds-limit", "wanted"),
			Job("earliest", "second") with { DueAt = DateTimeOffset.UnixEpoch.AddSeconds(-1) },
			Job("future", "second") with { State = JobState.Scheduled, DueAt = DateTimeOffset.UnixEpoch.AddHours(1) },
			Job("blocked", "paused") with { DueAt = DateTimeOffset.UnixEpoch.AddDays(-1) },
		], [], [], []);
		await storage.PauseJobAsync("paused", token);
		var request = Request(fair: false) with
		{
			BatchSize = 10,
			Queues = [new()
			{
				QueueName = "q", Capacity = 10,
				JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
				{
					["WANTED"] = 10, ["SECOND"] = 10, ["PAUSED"] = 10,
				},
			}],
		};
		Assert.Equal<string>(["earliest", "a-tie", "z-tie"],
			(await storage.AcquireDueJobsAsync(request, token)).Select(j => j.JobHandle.Value));
		await storage.CompleteAsync(JobHandle.FromString("a-tie"), 1, "worker", token);
		await RegisterAsync(storage, definitions.Select(d => d with { QueueName = "moved" }).ToList(), token);
		Assert.Empty(await storage.AcquireDueJobsAsync(request, token));
		var moved = request with { Queues = [request.Queues[0] with { QueueName = "moved" }] };
		Assert.Equal("b-exceeds-limit", Assert.Single(await storage.AcquireDueJobsAsync(moved, token)).JobHandle.Value);
	}

	[Fact]
	public async Task RetryAndCancellationKeepRunnableAndLeaseMembershipCurrent()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, [new() { Name = "wanted", QueueName = "q" }], token);
		var handle = JobHandle.FromString("retry");
		await storage.EnqueueAsync(Submission(handle, "wanted"), token);
		var claimed = Assert.Single(await storage.AcquireDueJobsAsync(Request(fair: false), token));
		await storage.FailAsync(handle, claimed.Attempt, "worker", "retry later", clock.GetUtcNow().AddHours(1), token);
		Assert.Empty(await storage.AcquireDueJobsAsync(Request(fair: false), token));
		await storage.RetryAsync(handle, token);
		claimed = Assert.Single(await storage.AcquireDueJobsAsync(Request(fair: false), token));
		await storage.FailAsync(handle, claimed.Attempt, "worker", "failed", nextRetryAt: null, token);
		Assert.Empty(await storage.AcquireDueJobsAsync(Request(fair: false), token));
		await storage.RetryAsync(handle, token);
		claimed = Assert.Single(await storage.AcquireDueJobsAsync(Request(fair: false), token));
		await storage.CancelAsync(handle, token);
		clock.Advance(TimeSpan.FromDays(1));
		Assert.Empty(await storage.AcquireDueJobsAsync(Request(fair: false), token));
		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(handle, token))!.State);
		Assert.Equal(0, (await storage.GetJobAcquisitionStateAsync("wanted", token)).ActiveCount);
	}

	private static JobSubmission Submission(JobHandle handle, string name) => new()
	{
		JobHandle = handle,
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		CreatedAt = DateTimeOffset.UnixEpoch,
		DueAt = DateTimeOffset.UnixEpoch,
	};

	private static ValueTask RegisterAsync(InMemoryJobStorage storage, IReadOnlyList<JobDefinitionRecord> definitions, CancellationToken token) =>
		storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = definitions,
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);

	private static JobRecord Job(string id, string name = "work") => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		CreatedAt = DateTimeOffset.UnixEpoch,
		DueAt = DateTimeOffset.UnixEpoch,
	};

	private static JobRecord Active(string id, string name, string group) => Job(id, name) with
	{
		State = JobState.Active,
		GroupId = group,
		WorkerId = "worker",
		LeaseExpiresAt = DateTimeOffset.MaxValue,
	};

	private static JobAcquisitionRequest Request(bool fair) => new()
	{
		WorkerId = "worker",
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = 1,
		FairQueues = fair ? new() { GroupRoundRobin = false, ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = 1 } : null,
		Queues = [new()
		{
			QueueName = "q", Capacity = 1,
			JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wanted"] = 1 },
		}],
	};

	private static JobSubmission Job(string id, string name, string? group, DateTimeOffset createdAt) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		GroupId = group,
		CreatedAt = createdAt,
		DueAt = DateTimeOffset.UnixEpoch,
	};

	private static JobAcquisitionRequest Request(int limitedCapacity, bool roundRobin) => new()
	{
		WorkerId = "worker",
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = 10,
		FairQueues = new() { GroupRoundRobin = roundRobin, ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = roundRobin ? 30 : 1 },
		Queues =
		[
			new()
			{
				QueueName = "default",
				Capacity = 10,
				JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { ["LIMITED"] = limitedCapacity, ["free"] = 10 },
			},
		],
	};

	private sealed class SnapshotCountingStorage(TimeProvider clock) : CapturingJobStorage(clock)
	{
		public int SnapshotReads { get; private set; }
		public int DefinitionReads { get; private set; }

		public override ValueTask<JobMonitoringDefinitions> GetMonitoringDefinitionsAsync(CancellationToken cancellationToken = default)
		{
			DefinitionReads++;
			return base.GetMonitoringDefinitionsAsync(cancellationToken);
		}

		public override ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
		{
			SnapshotReads++;
			return base.GetMonitoringSnapshotAsync(cancellationToken);
		}
	}
}

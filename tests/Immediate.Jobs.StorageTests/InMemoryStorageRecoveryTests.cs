using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryStorageRecoveryTests
{
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

	private static JobRecord Job(string id) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = "work",
		Payload = "{}",
		State = JobState.Pending,
		CreatedAt = DateTimeOffset.UnixEpoch,
		DueAt = DateTimeOffset.UnixEpoch,
	};

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

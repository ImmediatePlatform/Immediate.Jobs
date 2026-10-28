using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryProvisionalAcquisitionTests
{
	[Theory]
	[InlineData(false, 0)]
	[InlineData(false, 1)]
	[InlineData(false, 2)]
	[InlineData(false, 3)]
	[InlineData(true, 0)]
	[InlineData(true, 1)]
	[InlineData(true, 2)]
	[InlineData(true, 3)]
	public async Task ProvisionalSelectionMatchesAcquisitionWithoutConsumingState(bool fair, int limit)
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, Definition(limit), token);
		var batch = BatchHandle.FromString("batch");
		JobRecord[] jobs = [.. Enumerable.Range(0, 4).Select(index => Job(string.Create(CultureInfo.InvariantCulture, $"job-{index}")) with
		{
			BatchHandle = batch,
			GroupId = index < 2 ? "a" : "b",
			CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(index),
		})];
		storage.LoadPersistedJobState(jobs, [new() { BatchHandle = batch, State = BatchState.Executing, TotalJobs = 4, PendingCount = 4, CreatedAt = DateTimeOffset.UnixEpoch }], [], []);
		var request = Request(2, fair);
		var beforeJobs = await storage.QueryJobsAsync(new(), token);
		var beforeLimits = await storage.GetJobAcquisitionStateAsync("work", token);

		var selected = await storage.ProvisionallyAcquireDueJobsAsync(request, token);

		Assert.Equal(2, selected.Count);
		Assert.Equal(fair ? [jobs[0].JobHandle, jobs[2].JobHandle] : [jobs[0].JobHandle, jobs[1].JobHandle], selected);
		Assert.Equal(selected, await storage.ProvisionallyAcquireDueJobsAsync(request, token));
		Assert.Equal(beforeJobs, await storage.QueryJobsAsync(new(), token));
		Assert.Equal(beforeLimits, await storage.GetJobAcquisitionStateAsync("work", token));
		Assert.Null((await storage.GetBatchStatusAsync(batch, token))!.StartedAt);
		foreach (var job in jobs)
			Assert.Empty(await storage.QueryJobExecutionsAsync(job.JobHandle, new(), token));

		var acquired = await storage.AcquireDueJobsAsync(request, token);

		Assert.Equal(selected, acquired.Select(static job => job.JobHandle));
		Assert.All(acquired, static job => Assert.Equal(1, job.Attempt));
		Assert.NotNull((await storage.GetBatchStatusAsync(batch, token))!.StartedAt);
		if (limit != 0)
			Assert.Empty(await storage.ProvisionallyAcquireDueJobsAsync(request, token));
	}

	[Fact]
	public async Task RepeatedQueueEntriesDoNotSelectTheSameJobTwice()
	{
		var token = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		await RegisterAsync(storage, Definition(0), token);
		storage.LoadPersistedJobState([Job("a"), Job("b")], [], [], []);
		var request = Request(1) with { BatchSize = 2 };
		request = request with { Queues = [request.Queues[0], request.Queues[0]] };

		Assert.Equal([JobHandle.FromString("a"), JobHandle.FromString("b")], await storage.ProvisionallyAcquireDueJobsAsync(request, token));
		Assert.Equal(2, (await storage.AcquireDueJobsAsync(request, token)).Count);
	}

	[Fact]
	public async Task ExactMemoryClaimsPreserveOrderAndSkipDuplicateOrIneligibleHandles()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, Definition(0), token);
		var a = Job("a");
		var b = Job("b");
		var future = Job("future") with { State = JobState.Scheduled, DueAt = clock.GetUtcNow().AddHours(1) };
		storage.LoadPersistedJobState([a, b, future], [], [], []);

		var acquired = await storage.AcquireJobsAsync([b.JobHandle, b.JobHandle, future.JobHandle, JobHandle.FromString("missing"), a.JobHandle], "worker", TimeSpan.FromMinutes(1), token);

		Assert.Equal([b.JobHandle, a.JobHandle], acquired.Select(static job => job.JobHandle));
		Assert.Equal(2, (await storage.GetJobAcquisitionStateAsync("work", token)).ActiveCount);
		foreach (var job in acquired)
		{
			Assert.Equal("worker", job.WorkerId);
			Assert.Equal(1, Assert.Single(await storage.QueryJobExecutionsAsync(job.JobHandle, new(), token)).Attempt);
		}

		clock.Advance(TimeSpan.FromMinutes(2));
		Assert.Empty(await storage.AcquireDueJobsAsync(Request(2), token));
		var reclaimed = await storage.AcquireJobsAsync([a.JobHandle, b.JobHandle], "other", TimeSpan.FromMinutes(1), token);
		Assert.Equal(2, reclaimed.Count);
		Assert.All(reclaimed, static job => Assert.Equal(2, job.Attempt));
		Assert.Equal(JobExecutionState.Interrupted, Assert.Single(await storage.QueryJobExecutionsAsync(a.JobHandle, new() { Attempt = 1 }, token)).State);
	}

	[Fact]
	public async Task ProvisionalReclaimObservesRenewalsWithoutInterruptingExecutions()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, Definition(1), token);
		storage.LoadPersistedJobState([Job("a"), Job("b"), Job("c")], [], [], []);
		await storage.AcquireJobsAsync([JobHandle.FromString("a"), JobHandle.FromString("b")], "worker", TimeSpan.FromMinutes(1), token);
		clock.Advance(TimeSpan.FromMinutes(1));
		storage.ApplyRenewedLease(JobHandle.FromString("a"), 1, "worker", TimeSpan.FromMinutes(2));

		Assert.Equal([JobHandle.FromString("b")], await storage.ProvisionallyAcquireDueJobsAsync(Request(3), token));
		Assert.Equal(JobExecutionState.Active, Assert.Single(await storage.QueryJobExecutionsAsync(JobHandle.FromString("b"), new(), token)).State);
		Assert.Equal(1, (await storage.GetJobStatusAsync(JobHandle.FromString("b"), token))!.Attempt);
		await storage.CompleteAsync(JobHandle.FromString("a"), 1, "worker", token);
		await storage.CancelAsync(JobHandle.FromString("b"), token);
		Assert.Equal([JobHandle.FromString("c")], await storage.ProvisionallyAcquireDueJobsAsync(Request(3), token));
	}

	[Fact]
	public async Task SingleServerUsesExactDurableClaimsAndRetainsSelectionAfterFailure()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durable = new ExactClaimStorage(clock);
		await RegisterAsync(durable, Definition(0), token);
		durable.LoadPersistedJobState([Job("a-1") with { GroupId = "a" }, Job("a-2") with { GroupId = "a" }, Job("b-1") with { GroupId = "b" }], [], [], []);
		await using var writer = new SingleServerJobStorage(durable, clock, logger: null);
		await writer.InitializeAsync(interruptActiveLeases: true, token);
		durable.FailNext = true;
		durable.BeforeClaim = async () =>
		{
			var jobs = await writer.QueryJobsAsync(new(), token);
			Assert.All(jobs, static job => Assert.Equal(JobState.Pending, job.State));
			Assert.All(jobs, static job => Assert.Equal(0, job.Attempt));
		};

		await Assert.ThrowsAsync<IOException>(() => writer.AcquireDueJobsAsync(Request(2, fair: true), token).AsTask());
		var firstSelection = durable.Requested;
		var acquired = await writer.AcquireDueJobsAsync(Request(2, fair: true), token);

		Assert.Equal(firstSelection, durable.Requested);
		Assert.Equal(firstSelection, acquired.Select(static job => job.JobHandle));
		Assert.Equal(2, durable.ExactCalls);
		Assert.All(acquired, static job => Assert.Equal(1, job.Attempt));
		Assert.Equal(2, (await writer.QueryJobsAsync(new() { State = JobState.Active }, token)).Count);
	}

	[Fact]
	public async Task PartialClaimsAdvanceFairnessOnlyForConfirmedJobs()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durable = new ExactClaimStorage(clock) { MaximumClaims = 1 };
		await RegisterAsync(durable, Definition(0), token);
		durable.LoadPersistedJobState([Job("a-1") with { GroupId = "a" }, Job("a-2") with { GroupId = "a" }, Job("b-1") with { GroupId = "b" }], [], [], []);
		await using var writer = new SingleServerJobStorage(durable, clock, logger: null);
		await writer.InitializeAsync(interruptActiveLeases: true, token);

		var first = Assert.Single(await writer.AcquireDueJobsAsync(Request(2, fair: true), token));
		var second = Assert.Single(await writer.AcquireDueJobsAsync(Request(1, fair: true), token));

		Assert.Equal("a", first.GroupId);
		Assert.Equal("b", second.GroupId);
		Assert.Equal(1, second.Attempt);
		Assert.Equal(JobState.Pending, (await writer.GetJobStatusAsync(JobHandle.FromString("a-2"), token))!.State);
	}

	[Fact]
	public async Task SingleServerRestoresPausesAndMirrorsResumeWithoutDurablePolling()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durable = new ExactClaimStorage(clock);
		await RegisterAsync(durable, Definition(0), token);
		durable.LoadPersistedJobState([Job("a"), Job("b")], [], [], []);
		await durable.PauseJobAsync("work", token);
		await using var writer = new SingleServerJobStorage(durable, clock, logger: null);
		await writer.InitializeAsync(interruptActiveLeases: true, token);

		Assert.Empty(await writer.AcquireDueJobsAsync(Request(1), token));
		Assert.Equal(0, durable.ExactCalls);
		await writer.ResumeJobAsync("work", token);
		Assert.Single(await writer.AcquireDueJobsAsync(Request(1), token));
		await writer.PauseJobAsync("work", token);
		Assert.Empty(await writer.AcquireDueJobsAsync(Request(1), token));
		Assert.Equal(1, durable.ExactCalls);
	}

	[Fact]
	public async Task SingleServerRetainsDurableSlidingHistoryAcrossRecoveryAndPartialClaims()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durable = new ExactClaimStorage(clock);
		await RegisterAsync(durable, Definition(2), token);
		durable.LoadPersistedJobState([Job("previous"), Job("a"), Job("b"), Job("c")], [], [], []);
		var previous = Assert.Single(await durable.AcquireJobsAsync([JobHandle.FromString("previous")], "previous", TimeSpan.FromMinutes(1), token));
		await durable.CompleteAsync(previous.JobHandle, previous.Attempt, "previous", token);
		clock.Advance(TimeSpan.FromSeconds(10));
		await using var writer = new SingleServerJobStorage(durable, clock, logger: null);
		await writer.InitializeAsync(interruptActiveLeases: true, token);

		Assert.Single(await writer.AcquireDueJobsAsync(Request(3), token));
		var calls = durable.ExactCalls;
		Assert.Empty(await writer.AcquireDueJobsAsync(Request(3), token));
		Assert.Equal(calls, durable.ExactCalls);
		clock.Advance(TimeSpan.FromSeconds(50));
		Assert.Single(await writer.AcquireDueJobsAsync(Request(3), token));
		Assert.Empty(await writer.AcquireDueJobsAsync(Request(3), token));
		clock.Advance(TimeSpan.FromSeconds(10));
		Assert.Single(await writer.AcquireDueJobsAsync(Request(3), token));
	}

	[Fact]
	public async Task CancellationAfterDurableCommitStillUpdatesMemory()
	{
		var token = TestContext.Current.CancellationToken;
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var durable = new ExactClaimStorage(clock) { AfterCommit = cancellation.Cancel };
		await RegisterAsync(durable, Definition(0), token);
		durable.LoadPersistedJobState([Job("a")], [], [], []);
		await using var writer = new SingleServerJobStorage(durable, clock, logger: null);
		await writer.InitializeAsync(interruptActiveLeases: true, token);

		var acquired = Assert.Single(await writer.AcquireDueJobsAsync(Request(1), cancellation.Token));

		Assert.True(cancellation.IsCancellationRequested);
		Assert.Equal(JobState.Active, (await writer.GetJobStatusAsync(acquired.JobHandle, token))!.State);
		await writer.CompleteAsync(acquired.JobHandle, acquired.Attempt, "worker", token);
		Assert.Equal(JobState.Succeeded, (await writer.GetJobStatusAsync(acquired.JobHandle, token))!.State);
	}

	private static JobDefinitionRecord Definition(int limit) => new()
	{
		Name = "work",
		MaxConcurrency = limit == 1 ? 2 : 0,
		SlidingWindowMax = limit == 2 ? 2 : 0,
		SlidingWindowPeriod = limit == 2 ? TimeSpan.FromMinutes(1) : null,
		FixedWindowMax = limit == 3 ? 2 : 0,
		FixedWindowPeriod = limit == 3 ? TimeSpan.FromMinutes(1) : null,
	};

	private static ValueTask RegisterAsync(IJobStorage storage, JobDefinitionRecord definition, CancellationToken token) =>
		storage.MergeJobDefinitionsListAsync(new() { Definitions = [definition], RecurringSchedules = new Dictionary<string, DateTimeOffset>() }, token);

	private static JobRecord Job(string id) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = "work",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = DateTimeOffset.UnixEpoch,
		CreatedAt = DateTimeOffset.UnixEpoch,
	};

	private static JobAcquisitionRequest Request(int count, bool fair = false) => new()
	{
		WorkerId = "worker",
		BatchSize = count,
		Lease = TimeSpan.FromMinutes(1),
		Queues = [new() { QueueName = "default", Capacity = count, JobCapacities = new Dictionary<string, int> { ["work"] = count } }],
		FairQueues = fair ? new() { ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = 1, GroupRoundRobin = true } : null,
	};

	private sealed class ExactClaimStorage(TimeProvider clock) : CapturingJobStorage(clock)
	{
		public int ExactCalls { get; private set; }
		public IReadOnlyCollection<JobHandle> Requested { get; private set; } = [];
		public bool FailNext { get; set; }
		public int MaximumClaims { get; init; } = int.MaxValue;
		public Func<ValueTask>? BeforeClaim { get; set; }
		public Action? AfterCommit { get; init; }

		public override ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(JobAcquisitionRequest request, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("Single-server selection must use the in-memory indexes.");

		public override async ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(IReadOnlyCollection<JobHandle> jobs, string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
		{
			ExactCalls++;
			Requested = [.. jobs];
			if (BeforeClaim is { } before)
				await before();
			if (FailNext)
			{
				FailNext = false;
				throw new IOException("Durable claim failed before committing.");
			}

			var acquired = new List<JobRecord>();
			foreach (var handle in jobs.Take(MaximumClaims))
			{
				var job = await base.GetJobStatusAsync(handle, cancellationToken);
				if (job is null || (await base.GetJobAcquisitionStateAsync(job.JobName, cancellationToken)).AcquisitionStatus != JobAcquisitionStatus.Ready)
					continue;
				acquired.AddRange(await base.AcquireJobsAsync([handle], workerId, lease, cancellationToken));
			}

			AfterCommit?.Invoke();
			return acquired;
		}

		public override ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(IReadOnlyCollection<JobHandle> childJobs, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<IReadOnlyList<JobContinuationEdge>>([]);
	}
}

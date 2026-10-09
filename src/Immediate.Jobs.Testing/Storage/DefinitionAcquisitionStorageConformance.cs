using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class DefinitionAcquisitionStorageConformance
{
	private const string Name = "limited-job";
	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new("Definitions.Pause.BlocksPendingAndNewJobsOnly", StorageCapabilities.Queue, PauseAsync),
		new("Definitions.SlidingWindow.EnforcesExactBoundaryAndRetainsHistoryAfterDeletion", StorageCapabilities.Queue, SlidingAsync),
		new("Definitions.SlidingWindow.ReleasesOnlyExpiredAcquisitions", StorageCapabilities.Queue, RollingAsync),
		new("Definitions.FixedWindow.ResetsAtAlignedBoundary", StorageCapabilities.Queue, FixedAsync),
		new("Definitions.Limits.CombineAndResumeWithoutReset", StorageCapabilities.Queue, CombinedAsync),
		new("Definitions.Concurrency.SharesSlotsAndRecoversExpiredLeases", StorageCapabilities.Queue, ConcurrencyAsync),
		new("Definitions.Concurrency.RenewalAndEarlyRelease", StorageCapabilities.Queue, RenewAsync),
		new("Definitions.Monitoring.ReportsPauseAndUnexpiredConcurrencyIndependently", StorageCapabilities.Queue, MonitoringAsync),
		new("Definitions.Acquisition.EnforcesLimitsUnderContention", StorageCapabilities.Queue, ContentionAsync),
		new("Definitions.Acquisition.SkipsBlockedDefinitionsInFairQueues", StorageCapabilities.Queue, FairAsync),
		new("Definitions.Retries.CountSuccessfulAcquisitionsOnly", StorageCapabilities.Queue, RetryAsync),
		new("Definitions.Recurring.MaterializesDuringExecutionPause", StorageCapabilities.Queue, RecurringAsync),
	];

	private static JobRecord Job(string id, DateTimeOffset now, string name = Name, string? group = null) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now,
		GroupId = group,
	};

	private static JobAcquisitionRequest Request(JobAcquisitionLimits limits, string worker = "worker", bool fair = false) => new()
	{
		WorkerId = worker,
		BatchSize = 10,
		Lease = TimeSpan.FromSeconds(30),
		Queues = [new() { QueueName = "default", Capacity = 10, JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [Name] = 10, ["other-job"] = 10 } }],
		JobLimits = new Dictionary<string, JobAcquisitionLimits>(StringComparer.Ordinal) { [Name] = limits },
		FairQueues = fair ? new() { ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = 1, GroupRoundRobin = true } : null,
	};

	private static void Equal<T>(T expected, T actual, string invariant) =>
		ConformanceAssert.Equal(expected, actual, "Definitions.Acquisition", invariant);

	private static async ValueTask MonitoringAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		Equal(2, (await storage.AcquireDueJobsAsync(Request(new()), token)).Count, "both jobs acquire without a concurrency limit");
		var state = await storage.GetJobAcquisitionStateAsync(Name, new(), token);
		Equal(2, state.ActiveCount, "unbounded definitions still expose actual active leases");
		Equal(expected: false, state.IsConcurrencyLimited, "unbounded concurrency is never exhausted");
		await storage.PauseJobAsync(Name, token);
		state = await storage.GetJobAcquisitionStateAsync(Name, new() { MaxConcurrency = 2 }, token);
		Equal(JobAcquisitionStatus.Paused, state.AcquisitionStatus, "pause has acquisition precedence");
		Equal(2, state.ActiveCount, "pause does not hide active leases");
		Equal(expected: true, state.IsConcurrencyLimited, "concurrency exhaustion remains visible during pause");
		clock.Advance(TimeSpan.FromSeconds(30));
		state = await storage.GetJobAcquisitionStateAsync(Name, new() { MaxConcurrency = 2 }, token);
		Equal(0, state.ActiveCount, "expired leases are excluded without requiring a new acquisition");
		Equal(expected: false, state.IsConcurrencyLimited, "capacity returns after the leases expire");
		Equal(expected: true, state.IsPaused, "expiration does not resume a paused definition");
	}

	private static async ValueTask PauseAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.EnqueueAsync(Job("running", clock.GetUtcNow()), token);
		var active = (await storage.AcquireDueJobsAsync(Request(new()), token)).Single();
		await storage.EnqueueAsync(Job("pending", clock.GetUtcNow()), token);
		await storage.PauseJobAsync(Name, token);
		await storage.PauseJobAsync(Name, token);
		await storage.EnqueueAsync(Job("new", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("other", clock.GetUtcNow(), "other-job"), token);
		var state = await storage.GetJobAcquisitionStateAsync(Name, new(), token);
		Equal(expected: true, state.IsPaused, "pause is persisted by job name");
		Equal(JobAcquisitionStatus.Paused, state.AcquisitionStatus, "paused definitions report their restriction");
		Equal("other-job", (await storage.AcquireDueJobsAsync(Request(new()), token)).Single().JobName, "a paused definition does not block other names");
		await storage.CompleteAsync(active.JobHandle, active.Attempt, "worker", token);
		await storage.ResumeJobAsync(Name, new(), token);
		await storage.ResumeJobAsync(Name, new(), token);
		Equal(2, (await storage.AcquireDueJobsAsync(Request(new()), token)).Count, "resume releases pending and newly created jobs");
	}

	private static async ValueTask SlidingAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var limits = new JobAcquisitionLimits { SlidingWindowMax = 1, SlidingWindowPeriod = TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(3) };
		var now = clock.GetUtcNow();
		await storage.EnqueueAsync(Job("one", now), token);
		await storage.EnqueueAsync(Job("two", now), token);
		var active = (await storage.AcquireDueJobsAsync(Request(limits), token)).Single();
		await storage.CompleteAsync(active.JobHandle, active.Attempt, "worker", token);
		await storage.DeleteAsync(active.JobHandle, token);
		Equal(now + limits.SlidingWindowPeriod, (await storage.GetJobAcquisitionStateAsync(Name, limits, token)).NextEligibleAt, "deletion must not erase sliding rate history or duration precision");
		clock.Advance(limits.SlidingWindowPeriod.Value - TimeSpan.FromTicks(1));
		Equal(0, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "sliding capacity remains exhausted until the exact boundary");
		clock.Advance(TimeSpan.FromTicks(1));
		Equal(JobAcquisitionStatus.Ready, (await storage.GetJobAcquisitionStateAsync(Name, limits, token)).AcquisitionStatus, "status refreshes without an acquisition");
		Equal(1, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "capacity returns at the exact sliding boundary");
		Equal(clock.GetUtcNow() + limits.SlidingWindowPeriod, (await storage.GetJobAcquisitionStateAsync(Name, limits, token)).NextEligibleAt, "new acquisition timestamps retain their fractional-second precision");
	}

	private static async ValueTask RollingAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var limits = new JobAcquisitionLimits { SlidingWindowMax = 2, SlidingWindowPeriod = TimeSpan.FromSeconds(10) };
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "first acquisition reserves one sliding slot");
		clock.Advance(TimeSpan.FromSeconds(5));
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "a later acquisition occupies a separate slot");
		await storage.EnqueueAsync(Job("three", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("four", clock.GetUtcNow()), token);
		Equal(0, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "the rolling window is full");
		clock.Advance(TimeSpan.FromSeconds(5));
		Equal(1, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "only the first acquisition expires after ten seconds");
		Equal(0, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "the window does not reset all its slots together");
		clock.Advance(TimeSpan.FromSeconds(5));
		Equal(1, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "the second slot returns at its own boundary");
	}

	private static async ValueTask FixedAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var period = TimeSpan.FromMinutes(1);
		var now = clock.GetUtcNow();
		var boundary = new DateTimeOffset(now.UtcTicks - now.UtcTicks % period.Ticks, TimeSpan.Zero) + period;
		clock.Advance(boundary - now - TimeSpan.FromTicks(1));
		var limits = new JobAcquisitionLimits { FixedWindowMax = 1, FixedWindowPeriod = period };
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "one acquisition is permitted in the old fixed window");
		Equal(boundary, (await storage.GetJobAcquisitionStateAsync(Name, limits, token)).NextEligibleAt, "fixed windows align to UTC boundaries");
		clock.Advance(TimeSpan.FromTicks(1));
		Equal(1, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "fixed capacity resets at the aligned boundary");
	}

	private static async ValueTask CombinedAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var limits = new JobAcquisitionLimits { SlidingWindowMax = 1, SlidingWindowPeriod = TimeSpan.FromMinutes(2), FixedWindowMax = 1, FixedWindowPeriod = TimeSpan.FromMinutes(1) };
		var now = clock.GetUtcNow();
		await storage.EnqueueAsync(Job("one", now), token);
		await storage.EnqueueAsync(Job("two", now), token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "all configured limits apply to one acquisition");
		await storage.PauseJobAsync(Name, token);
		Equal(JobAcquisitionStatus.Paused, (await storage.GetJobAcquisitionStateAsync(Name, limits, token)).AcquisitionStatus, "pause takes precedence over exhausted limits");
		await storage.ResumeJobAsync(Name, limits, token);
		var state = await storage.GetJobAcquisitionStateAsync(Name, limits, token);
		Equal(JobAcquisitionStatus.RateLimited, state.AcquisitionStatus, "resume preserves exhausted counters");
		Equal(now.AddMinutes(2), state.NextEligibleAt, "next eligibility satisfies both windows");
	}

	private static async ValueTask ConcurrencyAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var limits = new JobAcquisitionLimits { MaxConcurrency = 1 };
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		var first = (await storage.AcquireDueJobsAsync(Request(limits, "first"), token)).Single();
		Equal(0, (await storage.AcquireDueJobsAsync(Request(limits, "second"), token)).Count, "workers share the definition's concurrency capacity");
		Equal(JobAcquisitionStatus.ConcurrencyLimited, (await storage.GetJobAcquisitionStateAsync(Name, limits, token)).AcquisitionStatus, "active leases report concurrency restriction");
		clock.Advance(TimeSpan.FromSeconds(30));
		Equal(JobAcquisitionStatus.Ready, (await storage.GetJobAcquisitionStateAsync(Name, limits, token)).AcquisitionStatus, "expired leases release concurrency capacity");
		var next = (await storage.AcquireDueJobsAsync(Request(limits, "second"), token)).Single();
		Equal(first.JobHandle, next.JobHandle, "the expired invocation can be reacquired");
		Equal(2, next.Attempt, "lease recovery creates another execution acquisition");
	}

	private static async ValueTask RenewAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var limits = new JobAcquisitionLimits { MaxConcurrency = 1 };
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		var first = (await storage.AcquireDueJobsAsync(Request(limits), token)).Single();
		await storage.RenewLeaseAsync(first.JobHandle, first.Attempt, "worker", TimeSpan.FromMinutes(2), token);
		clock.Advance(TimeSpan.FromSeconds(31));
		Equal(0, (await storage.AcquireDueJobsAsync(Request(limits, "second"), token)).Count, "renewed leases continue to occupy a slot");
		await storage.CompleteAsync(first.JobHandle, first.Attempt, "worker", token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(limits, "second"), token)).Count, "completion releases a slot before lease expiry");
	}

	private static async ValueTask ContentionAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var limits = new JobAcquisitionLimits { MaxConcurrency = 3, SlidingWindowMax = 3, SlidingWindowPeriod = TimeSpan.FromMinutes(1) };
		for (var index = 0; index < 12; index++)
			await storage.EnqueueAsync(Job(index.ToString(CultureInfo.InvariantCulture), clock.GetUtcNow()), token);
		var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => storage.AcquireDueJobsAsync(Request(limits, index.ToString(CultureInfo.InvariantCulture)), token).AsTask()));
		Equal(3, results.Sum(static jobs => jobs.Count), "contending workers cannot independently reserve the same rate or concurrency capacity");
	}

	private static async ValueTask FairAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var limits = new JobAcquisitionLimits { SlidingWindowMax = 1, SlidingWindowPeriod = TimeSpan.FromMinutes(1) };
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow(), group: "shared"), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow(), group: "shared"), token);
		await storage.EnqueueAsync(Job("other", clock.GetUtcNow().AddTicks(1), "other-job", "shared") with { DueAt = clock.GetUtcNow() }, token);
		var acquired = await storage.AcquireDueJobsAsync(Request(limits, fair: true), token);
		Equal(2, acquired.Count, "fair acquisition skips limited work without starving an eligible definition in the same group");
		Equal(1, acquired.Count(static job => string.Equals(job.JobName, Name, StringComparison.Ordinal)), "fair queues enforce definition limits within a batch");
	}

	private static async ValueTask RetryAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var limits = new JobAcquisitionLimits { SlidingWindowMax = 1, SlidingWindowPeriod = TimeSpan.FromSeconds(1) };
		await storage.EnqueueAsync(Job("retry", clock.GetUtcNow()), token);
		var first = (await storage.AcquireDueJobsAsync(Request(limits), token)).Single();
		await storage.FailAsync(first.JobHandle, first.Attempt, "worker", "retry", clock.GetUtcNow(), token);
		Equal(0, (await storage.AcquireDueJobsAsync(Request(limits), token)).Count, "blocked retry acquisitions consume no execution attempts");
		Equal(1, (await storage.GetJobStatusAsync(first.JobHandle, token))!.Attempt, "a rate-limited retry retains its attempt count");
		clock.Advance(TimeSpan.FromSeconds(1));
		var retry = (await storage.AcquireDueJobsAsync(Request(limits), token)).Single();
		Equal(2, retry.Attempt, "a successful retry acquisition creates a new attempt");
		Equal(JobAcquisitionStatus.RateLimited, (await storage.GetJobAcquisitionStateAsync(Name, limits, token)).AcquisitionStatus, "the retry consumes rate capacity");
	}

	private static async ValueTask RecurringAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var now = clock.GetUtcNow();
		var schedule = new RecurringJobSchedule { Name = "schedule", JobName = Name, QueueName = "default", Cron = "* * * * *", TimeZone = "UTC", NextRunAt = now, IsCodeDefined = false };
		await storage.UpsertRecurringAsync(schedule, token);
		await storage.PauseJobAsync(Name, token);
		var due = (await storage.GetDueRecurringAsync(now, 10, token)).Single();
		Equal(expected: true, await storage.MaterializeRecurringAsync(due, Job("occurrence", now) with { RecurringKey = "schedule:one" }, now.AddMinutes(1), cancellationToken: token), "execution pause permits recurring job creation");
		Equal(0, (await storage.AcquireDueJobsAsync(Request(new()), token)).Count, "the materialized occurrence remains paused");
		await storage.ResumeJobAsync(Name, new(), token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(new()), token)).Count, "resume releases the created occurrence");
	}
}

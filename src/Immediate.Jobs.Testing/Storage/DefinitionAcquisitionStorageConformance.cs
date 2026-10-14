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
		new("Definitions.Controls.RejectsMissingDefinitions", StorageCapabilities.Queue, MissingDefinitionsAsync),
		new("Definitions.Controls.ResolvesCaseInsensitiveNamesAndPreservesLimits", StorageCapabilities.Queue, CaseInsensitiveControlsAsync),
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

	private static JobSubmission Job(string id, DateTimeOffset now, string name = Name, string? group = null) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now,
		GroupId = group,
	};

	private static JobAcquisitionRequest Request(string worker = "worker", bool fair = false, string jobName = Name) => new()
	{
		WorkerId = worker,
		BatchSize = 10,
		Lease = TimeSpan.FromSeconds(30),
		Queues = [new() { QueueName = "default", Capacity = 10, JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [jobName] = 10, ["other-job"] = 10 } }],
		FairQueues = fair ? new() { ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = 1, GroupRoundRobin = true } : null,
	};

	private static void Equal<T>(T expected, T actual, string invariant) =>
		ConformanceAssert.Equal(expected, actual, "Definitions.Acquisition", invariant);

	private static async ValueTask MissingDefinitionsAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		const string CaseName = "Definitions.Controls.RejectsMissingDefinitions";
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [] }, token);
		await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => storage.PauseJobAsync(Name, token), CaseName, "pause requires a persisted definition");
		await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => storage.ResumeJobAsync(Name, token), CaseName, "resume requires a persisted definition");
		await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			async () => await storage.GetJobAcquisitionStateAsync(Name, token), CaseName, "status requires a persisted definition");
		Equal(0, (await storage.GetJobDefinitionsAsync(token)).Count, "controls do not create definition metadata");

		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [new() { Name = Name }] }, token);
		Equal(JobAcquisitionStatus.Ready, (await storage.GetJobAcquisitionStateAsync(Name, token)).AcquisitionStatus,
			"a failed pause must not leave a pause for a later registration");
	}

	private static async ValueTask CaseInsensitiveControlsAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		const string CanonicalName = "Émail.Σ";
		const string Alias = "ÉMAIL.Σ";
		var definition = new JobDefinitionRecord
		{
			Name = CanonicalName,
			MaxConcurrency = 1,
			SlidingWindowMax = 1,
			SlidingWindowPeriod = TimeSpan.FromMinutes(1),
		};
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [definition] }, token);
		await storage.EnqueueAsync(Job("alias-job", clock.GetUtcNow(), CanonicalName), token);
		Equal(CanonicalName, (await storage.AcquireDueJobsAsync(Request(jobName: Alias), token)).Single().JobName,
			"acquisition resolves the stored name from an alias");
		await storage.PauseJobAsync(Alias, token);
		Equal(JobAcquisitionStatus.Paused, (await storage.GetJobAcquisitionStateAsync(Alias, token)).AcquisitionStatus,
			"an alias pauses the same definition");
		await storage.ResumeJobAsync(Alias, token);
		var state = await storage.GetJobAcquisitionStateAsync(Alias, token);
		Equal(CanonicalName, state.JobName, "status retains the stored spelling");
		Equal(JobAcquisitionStatus.RateLimited, state.AcquisitionStatus, "resume retains rate history and stored limits");
		Equal(expected: true, state.IsConcurrencyLimited, "resume retains active leases and configured concurrency");
		Equal(definition.AcquisitionLimits, (await storage.GetJobDefinitionsAsync(token)).Single().AcquisitionLimits,
			"controls never rewrite definition configuration");
	}

	private static async ValueTask MonitoringAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var definition = new JobDefinitionRecord { Name = Name };
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);

		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		Equal(2, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "both jobs acquire without a concurrency limit");
		var state = await storage.GetJobAcquisitionStateAsync(Name, token);
		Equal(2, state.ActiveCount, "unbounded definitions still expose actual active leases");
		Equal(expected: false, state.IsConcurrencyLimited, "unbounded concurrency is never exhausted");
		await storage.PauseJobAsync(Name, token);
		definition = definition with { MaxConcurrency = 2 };
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [definition, new() { Name = "other-job" }] }, token);
		state = await storage.GetJobAcquisitionStateAsync(Name, token);
		Equal(JobAcquisitionStatus.Paused, state.AcquisitionStatus, "pause has acquisition precedence");
		Equal(2, state.ActiveCount, "pause does not hide active leases");
		Equal(expected: true, state.IsConcurrencyLimited, "concurrency exhaustion remains visible during pause");

		definition = definition with { MaxConcurrency = 3 };
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [definition, new() { Name = "other-job" }] }, token);
		state = await storage.GetJobAcquisitionStateAsync(Name, token);
		Equal(expected: false, state.IsConcurrencyLimited, "status uses the current persisted concurrency limit");
		Equal(expected: true, state.IsPaused, "configuration changes preserve pause state");
		await storage.ResumeJobAsync(Name, token);
		Equal(JobAcquisitionStatus.Ready, (await storage.GetJobAcquisitionStateAsync(Name, token)).AcquisitionStatus,
			"resume evaluates the current stored limit");
		Equal(definition.AcquisitionLimits, (await storage.GetJobDefinitionsAsync(token)).Single(item => string.Equals(item.Name, Name, StringComparison.Ordinal)).AcquisitionLimits,
			"resume preserves the stored concurrency setting");
		await storage.PauseJobAsync(Name, token);

		clock.Advance(TimeSpan.FromSeconds(30));
		state = await storage.GetJobAcquisitionStateAsync(Name, token);
		Equal(0, state.ActiveCount, "expired leases are excluded without requiring a new acquisition");
		Equal(expected: false, state.IsConcurrencyLimited, "capacity returns after the leases expire");
		Equal(expected: true, state.IsPaused, "expiration does not resume a paused definition");
	}

	private static async ValueTask PauseAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = Name }, new() { Name = "other-job" }],
		}, token);

		await storage.EnqueueAsync(Job("running", clock.GetUtcNow()), token);
		var active = (await storage.AcquireDueJobsAsync(Request(), token)).Single();
		await storage.EnqueueAsync(Job("pending", clock.GetUtcNow()), token);
		await storage.PauseJobAsync(Name, token);
		await storage.PauseJobAsync(Name, token);
		await storage.EnqueueAsync(Job("new", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("other", clock.GetUtcNow(), "other-job"), token);
		var state = await storage.GetJobAcquisitionStateAsync(Name, token);
		Equal(expected: true, state.IsPaused, "pause is persisted by job name");
		Equal(JobAcquisitionStatus.Paused, state.AcquisitionStatus, "paused definitions report their restriction");
		Equal("other-job", (await storage.AcquireDueJobsAsync(Request(), token)).Single().JobName, "a paused definition does not block other names");
		await storage.CompleteAsync(active.JobHandle, active.Attempt, "worker", token);
		await storage.ResumeJobAsync(Name, token);
		await storage.ResumeJobAsync(Name, token);
		Equal(2, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "resume releases pending and newly created jobs");
	}

	private static async ValueTask SlidingAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var period = TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(3);
		var definition = new JobDefinitionRecord
		{
			Name = Name,
			SlidingWindowMax = 1,
			SlidingWindowPeriod = period,
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);
		var now = clock.GetUtcNow();
		await storage.EnqueueAsync(Job("one", now), token);
		await storage.EnqueueAsync(Job("two", now), token);
		var active = (await storage.AcquireDueJobsAsync(Request(), token)).Single();
		await storage.CompleteAsync(active.JobHandle, active.Attempt, "worker", token);
		await storage.DeleteAsync(active.JobHandle, token);
		Equal(now + period, (await storage.GetJobAcquisitionStateAsync(Name, token)).NextEligibleAt, "deletion must not erase sliding rate history or duration precision");
		clock.Advance(period - TimeSpan.FromMilliseconds(1));
		Equal(0, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "sliding capacity remains exhausted until the exact boundary");
		clock.Advance(TimeSpan.FromMilliseconds(1));
		Equal(JobAcquisitionStatus.Ready, (await storage.GetJobAcquisitionStateAsync(Name, token)).AcquisitionStatus, "status refreshes without an acquisition");
		Equal(1, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "capacity returns at the exact sliding boundary");
		Equal(clock.GetUtcNow() + period, (await storage.GetJobAcquisitionStateAsync(Name, token)).NextEligibleAt, "new acquisition timestamps retain their fractional-second precision");
	}

	private static async ValueTask RollingAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var definition = new JobDefinitionRecord
		{
			Name = Name,
			SlidingWindowMax = 2,
			SlidingWindowPeriod = TimeSpan.FromSeconds(10),
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "first acquisition reserves one sliding slot");
		clock.Advance(TimeSpan.FromSeconds(5));
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "a later acquisition occupies a separate slot");
		await storage.EnqueueAsync(Job("three", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("four", clock.GetUtcNow()), token);
		Equal(0, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "the rolling window is full");
		clock.Advance(TimeSpan.FromSeconds(5));
		Equal(1, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "only the first acquisition expires after ten seconds");
		Equal(0, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "the window does not reset all its slots together");
		clock.Advance(TimeSpan.FromSeconds(5));
		Equal(1, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "the second slot returns at its own boundary");
	}

	private static async ValueTask FixedAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var period = TimeSpan.FromMinutes(1);
		var now = clock.GetUtcNow();
		var boundary = new DateTimeOffset(now.UtcTicks - now.UtcTicks % period.Ticks, TimeSpan.Zero) + period;
		clock.Advance(boundary - now - TimeSpan.FromMilliseconds(1));
		var definition = new JobDefinitionRecord
		{
			Name = Name,
			FixedWindowMax = 1,
			FixedWindowPeriod = period,
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "one acquisition is permitted in the old fixed window");
		Equal(boundary, (await storage.GetJobAcquisitionStateAsync(Name, token)).NextEligibleAt, "fixed windows align to UTC boundaries");
		clock.Advance(TimeSpan.FromMilliseconds(1));
		Equal(1, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "fixed capacity resets at the aligned boundary");
	}

	private static async ValueTask CombinedAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var definition = new JobDefinitionRecord
		{
			Name = Name,
			SlidingWindowMax = 1,
			SlidingWindowPeriod = TimeSpan.FromMinutes(2),
			FixedWindowMax = 1,
			FixedWindowPeriod = TimeSpan.FromMinutes(1),
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);
		var now = clock.GetUtcNow();
		await storage.EnqueueAsync(Job("one", now), token);
		await storage.EnqueueAsync(Job("two", now), token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "all configured limits apply to one acquisition");
		await storage.PauseJobAsync(Name, token);
		Equal(JobAcquisitionStatus.Paused, (await storage.GetJobAcquisitionStateAsync(Name, token)).AcquisitionStatus, "pause takes precedence over exhausted limits");
		await storage.ResumeJobAsync(Name, token);
		var state = await storage.GetJobAcquisitionStateAsync(Name, token);
		Equal(JobAcquisitionStatus.RateLimited, state.AcquisitionStatus, "resume preserves exhausted counters");
		Equal(now.AddMinutes(2), state.NextEligibleAt, "next eligibility satisfies both windows");
	}

	private static async ValueTask ConcurrencyAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var definition = new JobDefinitionRecord
		{
			Name = Name,
			MaxConcurrency = 1,
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		var first = (await storage.AcquireDueJobsAsync(Request("first"), token)).Single();
		Equal(0, (await storage.AcquireDueJobsAsync(Request("second"), token)).Count, "workers share the definition's concurrency capacity");
		Equal(JobAcquisitionStatus.ConcurrencyLimited, (await storage.GetJobAcquisitionStateAsync(Name, token)).AcquisitionStatus, "active leases report concurrency restriction");
		clock.Advance(TimeSpan.FromSeconds(30));
		Equal(JobAcquisitionStatus.Ready, (await storage.GetJobAcquisitionStateAsync(Name, token)).AcquisitionStatus, "expired leases release concurrency capacity");
		var next = (await storage.AcquireDueJobsAsync(Request("second"), token)).Single();
		Equal(first.JobHandle, next.JobHandle, "the expired invocation can be reacquired");
		Equal(2, next.Attempt, "lease recovery creates another execution acquisition");
	}

	private static async ValueTask RenewAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var definition = new JobDefinitionRecord
		{
			Name = Name,
			MaxConcurrency = 1,
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow()), token);
		var first = (await storage.AcquireDueJobsAsync(Request(), token)).Single();
		await storage.RenewLeaseAsync(first.JobHandle, first.Attempt, "worker", TimeSpan.FromMinutes(2), token);
		clock.Advance(TimeSpan.FromSeconds(31));
		Equal(0, (await storage.AcquireDueJobsAsync(Request("second"), token)).Count, "renewed leases continue to occupy a slot");
		await storage.CompleteAsync(first.JobHandle, first.Attempt, "worker", token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request("second"), token)).Count, "completion releases a slot before lease expiry");
	}

	private static async ValueTask ContentionAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var definition = new JobDefinitionRecord
		{
			Name = Name,
			MaxConcurrency = 3,
			SlidingWindowMax = 3,
			SlidingWindowPeriod = TimeSpan.FromMinutes(1),
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);
		for (var index = 0; index < 12; index++)
			await storage.EnqueueAsync(Job(index.ToString(CultureInfo.InvariantCulture), clock.GetUtcNow()), token);
		var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => storage.AcquireDueJobsAsync(Request(index.ToString(CultureInfo.InvariantCulture)), token).AsTask()));
		Equal(3, results.Sum(static jobs => jobs.Count), "contending workers cannot independently reserve the same rate or concurrency capacity");
	}

	private static async ValueTask FairAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var definition = new JobDefinitionRecord
		{
			Name = Name,
			SlidingWindowMax = 1,
			SlidingWindowPeriod = TimeSpan.FromMinutes(1),
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);
		await storage.EnqueueAsync(Job("one", clock.GetUtcNow(), group: "shared"), token);
		await storage.EnqueueAsync(Job("two", clock.GetUtcNow(), group: "shared"), token);
		await storage.EnqueueAsync(Job("other", clock.GetUtcNow().AddMilliseconds(1), "other-job", "shared") with { DueAt = clock.GetUtcNow() }, token);
		var acquired = await storage.AcquireDueJobsAsync(Request(fair: true), token);
		Equal(2, acquired.Count, "fair acquisition skips limited work without starving an eligible definition in the same group");
		Equal(1, acquired.Count(static job => string.Equals(job.JobName, Name, StringComparison.Ordinal)), "fair queues enforce definition limits within a batch");
	}

	private static async ValueTask RetryAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var definition = new JobDefinitionRecord
		{
			Name = Name,
			SlidingWindowMax = 1,
			SlidingWindowPeriod = TimeSpan.FromSeconds(1),
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition, new() { Name = "other-job" }],
		}, token);
		await storage.EnqueueAsync(Job("retry", clock.GetUtcNow()), token);
		var first = (await storage.AcquireDueJobsAsync(Request(), token)).Single();
		await storage.FailAsync(first.JobHandle, first.Attempt, "worker", "retry", clock.GetUtcNow(), token);
		Equal(0, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "blocked retry acquisitions consume no execution attempts");
		var status = ConformanceAssert.NotNull(await storage.GetJobStatusAsync(first.JobHandle, token),
			"Definitions.Retries.CountSuccessfulAcquisitionsOnly", "the rate-limited invocation must have a status");
		Equal(1, status.Attempt, "a rate-limited retry retains its attempt count");
		clock.Advance(TimeSpan.FromSeconds(1));
		var retry = (await storage.AcquireDueJobsAsync(Request(), token)).Single();
		Equal(2, retry.Attempt, "a successful retry acquisition creates a new attempt");
		Equal(JobAcquisitionStatus.RateLimited, (await storage.GetJobAcquisitionStateAsync(Name, token)).AcquisitionStatus, "the retry consumes rate capacity");
	}

	private static async ValueTask RecurringAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = Name }, new() { Name = "other-job" }],
		}, token);

		var now = clock.GetUtcNow();
		var schedule = new RecurringJobSchedule { Name = "schedule", JobName = Name, QueueName = "default", Cron = "* * * * *", TimeZone = "UTC", NextRunAt = now, IsCodeDefined = false };
		await storage.UpsertRecurringAsync(schedule, token);
		await storage.PauseJobAsync(Name, token);
		var due = (await storage.GetDueRecurringAsync(now, 10, [Name], token)).Single();
		Equal(expected: true, await storage.MaterializeRecurringAsync(due, new()
		{
			JobHandle = JobHandle.FromString("occurrence"),
			JobName = Name,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
			RecurringKey = "schedule:one",
		}, now.AddMinutes(1), cancellationToken: token), "execution pause permits recurring job creation");
		Equal(0, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "the materialized occurrence remains paused");
		await storage.ResumeJobAsync(Name, token);
		Equal(1, (await storage.AcquireDueJobsAsync(Request(), token)).Count, "resume releases the created occurrence");
	}
}

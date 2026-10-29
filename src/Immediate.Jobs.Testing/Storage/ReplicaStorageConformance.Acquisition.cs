using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

#pragma warning disable IDE0130
namespace Immediate.Jobs.Testing;

internal static partial class ReplicaStorageConformance
{
	private const string PartialClaimName = "Replica.Acquisition.AdvancesFairnessOnlyForConfirmedClaims";
	private const string PausedRecoveryName = "Replica.Acquisition.RestoresPausesAndMirrorsResumeWithoutDurablePolling";
	private const string SlidingRecoveryName = "Replica.Acquisition.RetainsSlidingHistoryAcrossRecoveryAndPartialClaims";
	private const string CommittedCancellationName = "Replica.Acquisition.MirrorsClaimsWhenCancellationArrivesAfterCommit";
	private const string RenewedSelectionName = "Replica.Acquisition.FailedReclaimsPreserveRenewedLeasesAndExecutionHistory";

	private enum AcquisitionLimit
	{
		Unbounded,
		Concurrency,
		SlidingWindow,
		FixedWindow,
	}

	private static IEnumerable<JobStorageConformanceTestCase> CreateAcquisitionCases()
	{
		bool[] policies = [false, true];
		foreach (var limit in Enum.GetValues<AcquisitionLimit>())
		{
			foreach (var fair in policies)
			{
				var name = string.Create(CultureInfo.InvariantCulture, $"Replica.Acquisition.FailedClaimsPreserve{limit}{(fair ? "Fair" : "Ordered")}State");
				yield return new(name, StorageCapabilities.Graph, (storage, clock, token) => FailedClaimsPreserveStateAsync(storage, clock, limit, fair, name, token));
			}
		}

		yield return new(PartialClaimName, StorageCapabilities.Graph, AdvancesOnlyConfirmedFairnessAsync);
		yield return new(PausedRecoveryName, StorageCapabilities.Graph, RestoresPausedAcquisitionAsync);
		yield return new(SlidingRecoveryName, StorageCapabilities.Graph, RetainsRecoveredSlidingHistoryAsync);
		yield return new(CommittedCancellationName, StorageCapabilities.Graph, MirrorsCommittedCancellationAsync);
		yield return new(RenewedSelectionName, StorageCapabilities.Graph, PreservesRenewedLeaseDuringFailedReclaimAsync);
	}

	private static async ValueTask FailedClaimsPreserveStateAsync(
		IJobStorage storage, FakeTimeProvider clock, AcquisitionLimit limit, bool fair, string caseName, CancellationToken token)
	{
		await RegisterAcquisitionAsync(storage, limit, token);
		var batch = BatchHandle.FromString("failed-claim-batch");
		string[] ids = ["first-a", "second-a", "first-b", "second-b"];
		var jobs = ids.Select((id, index) => Job(id, clock.GetUtcNow()) with
		{
			BatchHandle = batch,
			GroupId = index < 2 ? "a" : "b",
			CreatedAt = clock.GetUtcNow().AddMilliseconds(index),
		}).ToList();
		await Replica(storage, caseName).EnqueueBatchAsync(new()
		{
			BatchHandle = batch,
			State = BatchState.Executing,
			TotalJobs = 4,
			PendingCount = 4,
			CreatedAt = clock.GetUtcNow(),
		}, jobs, [], token);
		await using var durable = new AcquisitionReplicaStorage(Replica(storage, caseName)) { FailNextClaim = true };
		await using var provider = CreateSingleServerProvider(durable, clock);
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(interruptActiveLeases: false, token);
		var beforeJobs = await writer.QueryJobsAsync(new(), token);
		var beforeLimits = await writer.GetJobAcquisitionStateAsync(JobName, token);
		durable.BeforeClaim = async () =>
		{
			ConformanceAssert.SequenceEqual(beforeJobs, await writer.QueryJobsAsync(new(), token), caseName,
				"candidate selection must not change invocation ownership, attempts, or scheduling metadata before the durable claim");
			ConformanceAssert.Equal(beforeLimits, await writer.GetJobAcquisitionStateAsync(JobName, token), caseName,
				"candidate selection must not consume acquisition capacity");
			ConformanceAssert.Null((await Replica(writer, caseName).GetBatchStatusAsync(batch, token))!.StartedAt, caseName,
				"candidate selection must not start the batch before committing");
			foreach (var job in jobs)
				ConformanceAssert.Equal(0, (await writer.QueryJobExecutionsAsync(job.JobHandle, new(), token)).Count, caseName,
					"candidate selection must not create retained execution history");
		};

		await ConformanceAssert.ThrowsAsync<IOException>(async () => _ = await writer.AcquireDueJobsAsync(AcquisitionRequest(2, fair), token),
			caseName, "the durable claim failure must reach the caller");
		var selectedBeforeFailure = durable.LastRequested;
		var acquired = await writer.AcquireDueJobsAsync(AcquisitionRequest(2, fair), token);

		ConformanceAssert.SequenceEqual(fair ? [jobs[0].JobHandle, jobs[2].JobHandle] : [jobs[0].JobHandle, jobs[1].JobHandle],
			acquired.Select(static job => job.JobHandle), caseName, "queue order and fairness must select the expected two jobs");
		ConformanceAssert.SequenceEqual(selectedBeforeFailure, durable.LastRequested, caseName,
			"a failed claim must leave ordering, rate capacity, concurrency slots, and fairness turns available for retry");
		ConformanceAssert.SequenceEqual(selectedBeforeFailure, acquired.Select(static job => job.JobHandle), caseName,
			"the retry must commit exactly the originally selected handles");
		ConformanceAssert.True(acquired.All(static job => job.Attempt == 1), caseName, "a failed claim must not advance execution ordinals");
		ConformanceAssert.Equal(2, (await writer.QueryJobsAsync(new() { State = JobState.Active }, token)).Count, caseName,
			"only confirmed claims must become active in the mirror");
		durable.BeforeClaim = null;
		if (limit != AcquisitionLimit.Unbounded)
		{
			ConformanceAssert.Equal(0, (await writer.AcquireDueJobsAsync(AcquisitionRequest(2, fair), token)).Count, caseName,
				"successful claims must consume the configured capacity");
			ConformanceAssert.Equal(2, durable.ExactClaimCalls, caseName, "locally exhausted capacity must avoid another durable claim");
		}
	}

	private static async ValueTask AdvancesOnlyConfirmedFairnessAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await RegisterAcquisitionAsync(storage, AcquisitionLimit.Unbounded, token);
		await storage.EnqueueAsync(Job("a-1", clock.GetUtcNow()) with { GroupId = "a" }, token);
		await storage.EnqueueAsync(Job("a-2", clock.GetUtcNow()) with { GroupId = "a" }, token);
		await storage.EnqueueAsync(Job("b-1", clock.GetUtcNow()) with { GroupId = "b" }, token);
		await using var durable = new AcquisitionReplicaStorage(Replica(storage, PartialClaimName)) { MaximumClaims = 1 };
		await using var provider = CreateSingleServerProvider(durable, clock);
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(interruptActiveLeases: false, token);

		var first = (await writer.AcquireDueJobsAsync(AcquisitionRequest(2, fair: true), token)).Single();
		var second = (await writer.AcquireDueJobsAsync(AcquisitionRequest(1, fair: true), token)).Single();

		ConformanceAssert.Equal("a", first.GroupId, PartialClaimName, "the first ordered group must receive the partial claim");
		ConformanceAssert.Equal("b", second.GroupId, PartialClaimName, "unconfirmed selections must not consume a fairness turn");
		ConformanceAssert.Equal(1, second.Attempt, PartialClaimName, "an unconfirmed selection must retain its first execution ordinal");
		ConformanceAssert.Equal(JobState.Pending, (await GetJobAsync(writer, "a-2", PartialClaimName, token)).State, PartialClaimName,
			"an unclaimed sibling must remain pending");
	}

	private static async ValueTask RestoresPausedAcquisitionAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await RegisterAcquisitionAsync(storage, AcquisitionLimit.Unbounded, token);
		await storage.EnqueueAsync(Job("a", clock.GetUtcNow()), token);
		await storage.EnqueueAsync(Job("b", clock.GetUtcNow()), token);
		await storage.PauseJobAsync(JobName, token);
		await using var durable = new AcquisitionReplicaStorage(Replica(storage, PausedRecoveryName));
		await using var provider = CreateSingleServerProvider(durable, clock);
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(interruptActiveLeases: false, token);

		ConformanceAssert.Equal(0, (await writer.AcquireDueJobsAsync(AcquisitionRequest(1), token)).Count, PausedRecoveryName,
			"recovery must restore the persisted execution pause");
		ConformanceAssert.Equal(0, durable.ExactClaimCalls, PausedRecoveryName, "a restored pause must block local selection without durable polling");
		await writer.ResumeJobAsync(JobName, token);
		ConformanceAssert.Equal(1, (await writer.AcquireDueJobsAsync(AcquisitionRequest(1), token)).Count, PausedRecoveryName,
			"resume must update both durable controls and local selection");
		await writer.PauseJobAsync(JobName, token);
		ConformanceAssert.Equal(0, (await writer.AcquireDueJobsAsync(AcquisitionRequest(1), token)).Count, PausedRecoveryName,
			"a new pause must block local selection immediately");
		ConformanceAssert.Equal(1, durable.ExactClaimCalls, PausedRecoveryName, "pauses must avoid unnecessary durable claim attempts");
	}

	private static async ValueTask RetainsRecoveredSlidingHistoryAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await RegisterAcquisitionAsync(storage, AcquisitionLimit.SlidingWindow, token);
		foreach (var id in new[] { "previous", "a", "b", "c" })
			await storage.EnqueueAsync(Job(id, clock.GetUtcNow()), token);
		var previous = (await Replica(storage, SlidingRecoveryName).AcquireJobsAsync([JobHandle.FromString("previous")], "previous", TimeSpan.FromMinutes(1), token)).Single();
		await storage.CompleteAsync(previous.JobHandle, previous.Attempt, "previous", token);
		clock.Advance(TimeSpan.FromSeconds(10));
		await using var durable = new AcquisitionReplicaStorage(Replica(storage, SlidingRecoveryName));
		await using var provider = CreateSingleServerProvider(durable, clock);
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(interruptActiveLeases: false, token);

		var first = (await writer.AcquireDueJobsAsync(AcquisitionRequest(3), token)).Single();
		await writer.CompleteAsync(first.JobHandle, first.Attempt, "worker", token);
		var calls = durable.ExactClaimCalls;
		ConformanceAssert.Equal(0, (await writer.AcquireDueJobsAsync(AcquisitionRequest(3), token)).Count, SlidingRecoveryName,
			"recovered durable history must prevent oversubscribing the sliding window");
		ConformanceAssert.Equal(calls, durable.ExactClaimCalls, SlidingRecoveryName, "a known durable limit must suppress repeated rejected claims");
		clock.Advance(TimeSpan.FromSeconds(50));
		var second = (await writer.AcquireDueJobsAsync(AcquisitionRequest(3), token)).Single();
		await writer.CompleteAsync(second.JobHandle, second.Attempt, "worker", token);
		ConformanceAssert.Equal(0, (await writer.AcquireDueJobsAsync(AcquisitionRequest(3), token)).Count, SlidingRecoveryName,
			"only the expired acquisition must release capacity");
		clock.Advance(TimeSpan.FromSeconds(10));
		var third = (await writer.AcquireDueJobsAsync(AcquisitionRequest(3), token)).Single();
		ConformanceAssert.SequenceEqual(["a", "b", "c"], new[] { first, second, third }.Select(static job => job.JobHandle.Value), SlidingRecoveryName,
			"partial claims and recovery must preserve the remaining due jobs across window boundaries");
	}

	private static async ValueTask MirrorsCommittedCancellationAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
		await RegisterAcquisitionAsync(storage, AcquisitionLimit.Unbounded, token);
		await storage.EnqueueAsync(Job("committed", clock.GetUtcNow()), token);
		await using var durable = new AcquisitionReplicaStorage(Replica(storage, CommittedCancellationName)) { AfterClaim = cancellation.Cancel };
		await using var provider = CreateSingleServerProvider(durable, clock);
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(interruptActiveLeases: false, token);

		var acquired = (await writer.AcquireDueJobsAsync(AcquisitionRequest(1), cancellation.Token)).Single();

		ConformanceAssert.True(cancellation.IsCancellationRequested, CommittedCancellationName, "the durable commit must trigger cancellation before returning");
		ConformanceAssert.Equal(JobState.Active, (await GetJobAsync(writer, acquired.JobHandle, CommittedCancellationName, token)).State,
			CommittedCancellationName, "committed ownership must reach the mirror despite cancellation");
		await writer.CompleteAsync(acquired.JobHandle, acquired.Attempt, "worker", token);
		ConformanceAssert.Equal(JobState.Succeeded, (await GetJobAsync(storage, acquired.JobHandle, CommittedCancellationName, token)).State,
			CommittedCancellationName, "the mirrored claim must support completing the committed durable execution");
	}

	private static async ValueTask PreservesRenewedLeaseDuringFailedReclaimAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await RegisterAcquisitionAsync(storage, AcquisitionLimit.Concurrency, token);
		foreach (var id in new[] { "a", "b", "c" })
			await storage.EnqueueAsync(Job(id, clock.GetUtcNow()), token);
		await using var durable = new AcquisitionReplicaStorage(Replica(storage, RenewedSelectionName));
		await using var provider = CreateSingleServerProvider(durable, clock);
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(interruptActiveLeases: false, token);
		var initial = await writer.AcquireDueJobsAsync(AcquisitionRequest(2), token);
		ConformanceAssert.SequenceEqual(["a", "b"], initial.Select(static job => job.JobHandle.Value), RenewedSelectionName, "setup must acquire both concurrency slots");
		clock.Advance(TimeSpan.FromSeconds(30));
		await writer.RenewLeaseAsync(initial[0].JobHandle, 1, "worker", TimeSpan.FromMinutes(2), token);
		clock.Advance(TimeSpan.FromSeconds(30));
		durable.FailNextClaim = true;
		durable.BeforeClaim = async () =>
		{
			ConformanceAssert.SequenceEqual([initial[1].JobHandle], durable.LastRequested, RenewedSelectionName,
				"renewal must exclude the other owner from provisional reclamation");
			ConformanceAssert.Equal(1, (await GetJobAsync(writer, initial[1].JobHandle, RenewedSelectionName, token)).Attempt, RenewedSelectionName,
				"failed candidate selection must not increment the expired job's attempt");
			ConformanceAssert.Equal(JobExecutionState.Active, (await writer.QueryJobExecutionsAsync(initial[1].JobHandle, new(), token)).Single().State,
				RenewedSelectionName, "provisional reclamation must not interrupt retained execution history");
		};
		await ConformanceAssert.ThrowsAsync<IOException>(async () => _ = await writer.AcquireDueJobsAsync(AcquisitionRequest(3), token),
			RenewedSelectionName, "the failed reclaim must leave both owners unchanged");
		var reclaimed = (await writer.AcquireDueJobsAsync(AcquisitionRequest(3), token)).Single();
		durable.BeforeClaim = null;
		ConformanceAssert.Equal(initial[1].JobHandle, reclaimed.JobHandle, RenewedSelectionName, "only the expired owner must be reclaimed");
		ConformanceAssert.Equal(2, reclaimed.Attempt, RenewedSelectionName, "confirmed reclamation must increment the attempt exactly once");
		ConformanceAssert.Equal(JobExecutionState.Interrupted,
			(await writer.QueryJobExecutionsAsync(reclaimed.JobHandle, new() { Attempt = 1 }, token)).Single().State,
			RenewedSelectionName, "only a committed reclaim may interrupt the old execution");
		await writer.CompleteAsync(initial[0].JobHandle, 1, "worker", token);
		await writer.CancelAsync(reclaimed.JobHandle, token);
		ConformanceAssert.Equal("c", (await writer.AcquireDueJobsAsync(AcquisitionRequest(3), token)).Single().JobHandle.Value,
			RenewedSelectionName, "completion and cancellation must remove prior leases from the candidate index");
	}

	private static ValueTask RegisterAcquisitionAsync(IJobStorage storage, AcquisitionLimit limit, CancellationToken token) =>
		storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new()
			{
				Name = JobName,
				QueueName = QueueName,
				MaxConcurrency = limit == AcquisitionLimit.Concurrency ? 2 : 0,
				SlidingWindowMax = limit == AcquisitionLimit.SlidingWindow ? 2 : 0,
				SlidingWindowPeriod = limit == AcquisitionLimit.SlidingWindow ? TimeSpan.FromMinutes(1) : null,
				FixedWindowMax = limit == AcquisitionLimit.FixedWindow ? 2 : 0,
				FixedWindowPeriod = limit == AcquisitionLimit.FixedWindow ? TimeSpan.FromMinutes(1) : null,
			}],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);

	private static JobAcquisitionRequest AcquisitionRequest(int count, bool fair = false) => new()
	{
		WorkerId = "worker",
		BatchSize = count,
		Lease = TimeSpan.FromMinutes(1),
		Queues = [new() { QueueName = QueueName, Capacity = count, JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [JobName] = count } }],
		FairQueues = fair ? new() { ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = 1, GroupRoundRobin = true } : null,
	};

	private static ServiceProvider CreateSingleServerProvider(IJobStorage durable, FakeTimeProvider clock)
	{
		var services = new ServiceCollection();
		services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
		services.AddSingleton<TimeProvider>(clock);
		services.AddImmediateJobsCore().ConfigureStorage(builder => builder.UseSingleServer(_ => durable));
		return services.BuildServiceProvider();
	}
}

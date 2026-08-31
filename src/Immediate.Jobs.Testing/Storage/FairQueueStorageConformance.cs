using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;
using Microsoft.Extensions.Time.Testing;

#pragma warning disable IDE0130 // Public conformance APIs intentionally use the package root namespace.
namespace Immediate.Jobs.Testing;

internal static class FairQueueStorageConformance
{
	private const string CapabilityName = "FairQueues.Capability.ResolvesAdvertisedStorage";
	private const string RotationName = "FairQueues.Rotation.ServesNewGroupAheadOfServedBacklog";
	private const string InterleaveName = "FairQueues.Rotation.InterleavesGroupsWithinOneAcquisition";
	private const string NoisyName = "FairQueues.NoisyNeighbors.ServesQuietGroupFirst";
	private const string ExpiredName = "FairQueues.NoisyNeighbors.IgnoresExpiredLeases";
	private const string OrdinaryName = "FairQueues.Disabled.PreservesOrdinaryDueOrder";
	private const string ConcurrencyName = "FairQueues.Concurrency.ClaimsDistinctJobs";

	private static readonly FairQueuePolicy DefaultPolicy = new()
	{
		ConcurrencyShareThreshold = 0.10,
		MinInflightForNoisy = 30,
		GroupRoundRobin = true,
	};

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(CapabilityName, StorageCapabilities.FairQueues, ResolvesAdvertisedStorage),
		new(RotationName, StorageCapabilities.FairQueues, RotatesAcrossGroupsAsync),
		new(InterleaveName, StorageCapabilities.FairQueues, InterleavesGroupsAsync),
		new(NoisyName, StorageCapabilities.FairQueues, ServesQuietGroupAsync),
		new(ExpiredName, StorageCapabilities.FairQueues, IgnoresExpiredLeasesAsync),
		new(OrdinaryName, StorageCapabilities.FairQueues, NullPolicyPreservesOrderAsync),
		new(ConcurrencyName, StorageCapabilities.FairQueues, ConcurrentClaimsAreDistinctAsync),
	];

	private static ValueTask ResolvesAdvertisedStorage(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_ = GetFairStorage(storage, CapabilityName);
		return ValueTask.CompletedTask;
	}

	private static async ValueTask RotatesAcrossGroupsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, RotationName);
		var clock = timeProvider;
		await EnqueueAsync(storage, clock, "rotation-a-1", 0, "group-a", cancellationToken);
		await EnqueueAsync(storage, clock, "rotation-a-2", 1, "group-a", cancellationToken);
		var first = Single(
			await storage.AcquireDueJobsAsync(CreateRequest("rotation-worker-a", 1, DefaultPolicy), cancellationToken), RotationName, "the first fair acquisition must claim one job");
		await storage.CompleteAsync(first.JobHandle, first.Attempt, "rotation-worker-a", cancellationToken);
		await EnqueueAsync(storage, clock, "rotation-b-1", 2, "group-b", cancellationToken);
		var second = Single(
			await storage.AcquireDueJobsAsync(CreateRequest("rotation-worker-b", 1, DefaultPolicy), cancellationToken), RotationName, "the second fair acquisition must claim one job");
		ConformanceAssert.Equal("rotation-a-1", first.JobHandle.Value, RotationName, "ordinary due order selects the first group initially");
		ConformanceAssert.Equal("rotation-b-1", second.JobHandle.Value, RotationName,
			"a newly arrived group must advance ahead of previously served backlog");

		const string ExistingJobName = "fair-existing-groups";
		await EnqueueAsync(storage, clock, "existing-a-1", 3, "existing-a", cancellationToken, ExistingJobName);
		await EnqueueAsync(storage, clock, "existing-a-2", 4, "existing-a", cancellationToken, ExistingJobName);
		await EnqueueAsync(storage, clock, "existing-b-1", 5, "existing-b", cancellationToken, ExistingJobName);
		var existingFirst = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("existing-worker-a", 1, DefaultPolicy, jobName: ExistingJobName),
				cancellationToken
			),
			RotationName,
			"capacity-one fair acquisition must claim one existing-group job"
		);
		await storage.CompleteAsync(existingFirst.JobHandle, existingFirst.Attempt, "existing-worker-a", cancellationToken);
		var existingSecond = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("existing-worker-b", 1, DefaultPolicy, jobName: ExistingJobName),
				cancellationToken
			),
			RotationName,
			"the next fair acquisition must claim one existing-group job"
		);
		ConformanceAssert.SequenceEqual(
			["existing-a-1", "existing-b-1"],
			[existingFirst.JobHandle.Value, existingSecond.JobHandle.Value],
			RotationName,
			"capacity-one acquisitions must rotate across existing grouped backlogs"
		);

		const string ReturningJobName = "fair-returning-group";
		await EnqueueAsync(storage, clock, "returning-a-1", 6, "returning-a", cancellationToken, ReturningJobName);
		await EnqueueAsync(storage, clock, "returning-a-2", 7, "returning-a", cancellationToken, ReturningJobName);
		await EnqueueAsync(storage, clock, "returning-b-1", 8, "returning-b", cancellationToken, ReturningJobName);
		var returningFirst = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("returning-worker-a", 1, DefaultPolicy, jobName: ReturningJobName),
				cancellationToken
			),
			RotationName,
			"returning-group setup must claim its first job"
		);
		await storage.CompleteAsync(returningFirst.JobHandle, returningFirst.Attempt, "returning-worker-a", cancellationToken);
		var returningSecond = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("returning-worker-b", 1, DefaultPolicy, jobName: ReturningJobName),
				cancellationToken
			),
			RotationName,
			"returning-group setup must rotate to the second group"
		);
		await storage.CompleteAsync(returningSecond.JobHandle, returningSecond.Attempt, "returning-worker-b", cancellationToken);
		await EnqueueAsync(storage, clock, "returning-b-2", 9, "returning-b", cancellationToken, ReturningJobName);
		var returned = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("returning-worker-c", 1, DefaultPolicy, jobName: ReturningJobName),
				cancellationToken
			),
			RotationName,
			"a returning group must rejoin fair rotation"
		);
		ConformanceAssert.Equal(
			"returning-b-2",
			returned.JobHandle.Value,
			RotationName,
			"a cleared group must return without historical cursor debt"
		);
	}

	private static async ValueTask InterleavesGroupsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, InterleaveName);
		var clock = timeProvider;
		await EnqueueAsync(storage, clock, "interleave-a-1", 0, "group-a", cancellationToken);
		await EnqueueAsync(storage, clock, "interleave-a-2", 1, "group-a", cancellationToken);
		await EnqueueAsync(storage, clock, "interleave-b-1", 2, "group-b", cancellationToken);
		await EnqueueAsync(storage, clock, "interleave-b-2", 3, "group-b", cancellationToken);
		var acquired = await storage.AcquireDueJobsAsync(
			CreateRequest("interleave-worker", 4, DefaultPolicy), cancellationToken);
		ConformanceAssert.SequenceEqual(
			["interleave-a-1", "interleave-b-1", "interleave-a-2", "interleave-b-2"],
			acquired.Select(static job => job.JobHandle.Value),
			InterleaveName,
			"one fair acquisition must rotate among grouped backlogs",
			comparer: StringComparer.Ordinal
		);
	}

	private static async ValueTask ServesQuietGroupAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, NoisyName);
		var clock = timeProvider;
		await EnqueueAsync(storage, clock, "noisy-active-1", 0, "noisy", cancellationToken);
		await EnqueueAsync(storage, clock, "noisy-active-2", 1, "noisy", cancellationToken);
		var active = await storage.AcquireDueJobsAsync(
			CreateRequest("existing-worker", 2, policy: null), cancellationToken);
		ConformanceAssert.Equal(2, active.Count, NoisyName, "the noisy group setup must create two active jobs");
		await EnqueueAsync(storage, clock, "noisy-waiting", 2, "noisy", cancellationToken);
		await EnqueueAsync(storage, clock, "quiet-waiting", 3, "quiet", cancellationToken);
		var acquired = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("quiet-worker", 1, new()
				{
					ConcurrencyShareThreshold = 0.50,
					MinInflightForNoisy = 2,
					GroupRoundRobin = true,
				}), cancellationToken),
			NoisyName,
			"the noisy-neighbor acquisition must claim one job"
		);
		ConformanceAssert.Equal("quiet-waiting", acquired.JobHandle.Value, NoisyName,
			"a quiet group must be served before a group exceeding the in-flight threshold");
	}

	private static async ValueTask IgnoresExpiredLeasesAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, ExpiredName);
		var clock = timeProvider;
		await EnqueueAsync(storage, clock, "expired-1", 0, "formerly-noisy", cancellationToken, "expired-setup");
		await EnqueueAsync(storage, clock, "expired-2", 1, "formerly-noisy", cancellationToken, "expired-setup");
		var active = await storage.AcquireDueJobsAsync(
			CreateRequest("expired-worker", 2, policy: null, lease: TimeSpan.FromSeconds(1), jobName: "expired-setup"),
			cancellationToken);
		ConformanceAssert.Equal(2, active.Count, ExpiredName, "the expired-lease setup must claim two jobs");
		await EnqueueAsync(storage, clock, "formerly-noisy-waiting", 2, "formerly-noisy", cancellationToken);
		await EnqueueAsync(storage, clock, "quiet-after-expiry", 3, "quiet", cancellationToken);
		clock.Advance(TimeSpan.FromSeconds(2));
		var acquired = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("after-expiry-worker", 1, new()
				{
					ConcurrencyShareThreshold = 0.50,
					MinInflightForNoisy = 2,
					GroupRoundRobin = true,
				}), cancellationToken),
			ExpiredName,
			"the post-expiry acquisition must claim one job"
		);
		ConformanceAssert.Equal("formerly-noisy-waiting", acquired.JobHandle.Value, ExpiredName,
			"expired leases must not contribute to noisy-group classification");
	}

	private static async ValueTask NullPolicyPreservesOrderAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, OrdinaryName);
		var clock = timeProvider;
		await EnqueueAsync(storage, clock, "ordinary-a-1", 0, "group-a", cancellationToken);
		await EnqueueAsync(storage, clock, "ordinary-a-2", 1, "group-a", cancellationToken);
		await EnqueueAsync(storage, clock, "ordinary-b-1", 2, "group-b", cancellationToken);
		var first = Single(
			await storage.AcquireDueJobsAsync(CreateRequest("ordinary-worker-a", 1, policy: null), cancellationToken), OrdinaryName, "ordinary acquisition must claim one job");
		await storage.CompleteAsync(first.JobHandle, first.Attempt, "ordinary-worker-a", cancellationToken);
		var second = Single(
			await storage.AcquireDueJobsAsync(CreateRequest("ordinary-worker-b", 1, policy: null), cancellationToken), OrdinaryName, "second ordinary acquisition must claim one job");
		ConformanceAssert.SequenceEqual(
			["ordinary-a-1", "ordinary-a-2"],
			[first.JobHandle.Value, second.JobHandle.Value],
			OrdinaryName,
			"a null fair policy must retain ordinary due ordering",
			comparer: StringComparer.Ordinal
		);

		const string UngroupedJobName = "fair-ungrouped";
		await EnqueueAsync(storage, clock, "ungrouped-newer", 5, groupId: null, cancellationToken, UngroupedJobName);
		await EnqueueAsync(storage, clock, "ungrouped-oldest", 3, groupId: null, cancellationToken, UngroupedJobName);
		await EnqueueAsync(storage, clock, "ungrouped-middle", 4, groupId: null, cancellationToken, UngroupedJobName);
		var ungrouped = await storage.AcquireDueJobsAsync(
			CreateRequest("ungrouped-worker", 3, DefaultPolicy, jobName: UngroupedJobName),
			cancellationToken
		);
		ConformanceAssert.SequenceEqual(
			["ungrouped-oldest", "ungrouped-middle", "ungrouped-newer"],
			ungrouped.Select(static job => job.JobHandle.Value),
			OrdinaryName,
			"enabling fair queues must preserve due order for ungrouped jobs"
		);

		const string NoRoundRobinJobName = "fair-no-round-robin";
		await EnqueueAsync(storage, clock, "no-rr-a-1", 6, "no-rr-a", cancellationToken, NoRoundRobinJobName);
		await EnqueueAsync(storage, clock, "no-rr-a-2", 7, "no-rr-a", cancellationToken, NoRoundRobinJobName);
		await EnqueueAsync(storage, clock, "no-rr-b-1", 8, "no-rr-b", cancellationToken, NoRoundRobinJobName);
		var roundRobinFirst = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("no-rr-worker-a", 1, DefaultPolicy, jobName: NoRoundRobinJobName),
				cancellationToken
			),
			OrdinaryName,
			"round-robin setup must claim one job"
		);
		await storage.CompleteAsync(roundRobinFirst.JobHandle, roundRobinFirst.Attempt, "no-rr-worker-a", cancellationToken);
		var roundRobinDisabled = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("no-rr-worker-b", 1, new()
				{
					ConcurrencyShareThreshold = 0.10,
					MinInflightForNoisy = 30,
					GroupRoundRobin = false,
				}, jobName: NoRoundRobinJobName),
				cancellationToken
			),
			OrdinaryName,
			"disabling group round-robin must still claim one job"
		);
		ConformanceAssert.Equal(
			"no-rr-a-2",
			roundRobinDisabled.JobHandle.Value,
			OrdinaryName,
			"disabling group round-robin must restore ordinary due order"
		);

		await EnqueueAsync(storage, clock, "capacity-ungrouped-limited", 9, groupId: null, cancellationToken, "limited");
		await EnqueueAsync(storage, clock, "capacity-grouped-limited", 10, "capacity-a", cancellationToken, "limited");
		await EnqueueAsync(storage, clock, "capacity-other-1", 11, "capacity-b", cancellationToken, "other");
		await EnqueueAsync(storage, clock, "capacity-other-2", 12, "capacity-c", cancellationToken, "other");
		var capacityLimited = await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "capacity-worker",
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = 4,
			FairQueues = DefaultPolicy,
			Queues =
			[
				new()
				{
					QueueName = DefaultQueueName,
					Capacity = 2,
					JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal)
					{
						["limited"] = 1,
						["other"] = 10,
					},
				},
			],
		}, cancellationToken);
		ConformanceAssert.SequenceEqual(
			["capacity-ungrouped-limited", "capacity-other-1"],
			capacityLimited.Select(static job => job.JobHandle.Value),
			OrdinaryName,
			"fair acquisition must preserve ungrouped priority while honoring queue and job capacities"
		);
	}

	private static async ValueTask ConcurrentClaimsAreDistinctAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, ConcurrencyName);
		var clock = timeProvider;
		for (var index = 0; index < 12; index++)
		{
			await EnqueueAsync(
				storage,
				clock,
				string.Create(CultureInfo.InvariantCulture, $"concurrent-{index:D2}"),
				index,
				string.Create(CultureInfo.InvariantCulture, $"group-{index % 3}"),
				cancellationToken
			);
		}

		var claims = await Task.WhenAll(
			storage.AcquireDueJobsAsync(CreateRequest("concurrent-worker-a", 12, DefaultPolicy), cancellationToken).AsTask(),
			storage.AcquireDueJobsAsync(CreateRequest("concurrent-worker-b", 12, DefaultPolicy), cancellationToken).AsTask()
		);
		var ids = claims.SelectMany(static jobs => jobs).Select(static job => job.JobHandle.Value).ToList();
		ConformanceAssert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count(), ConcurrencyName,
			"concurrent fair acquisitions must never claim the same job twice");

		var remaining = await storage.AcquireDueJobsAsync(
			CreateRequest("concurrent-drain-worker", 12, DefaultPolicy),
			cancellationToken
		);
		var allIds = ids.Concat(remaining.Select(static job => job.JobHandle.Value)).ToList();
		ConformanceAssert.Equal(12, allIds.Count, ConcurrencyName,
			"every eligible job must remain claimable after concurrent acquisition contention");
		ConformanceAssert.Equal(12, allIds.Distinct(StringComparer.Ordinal).Count(), ConcurrencyName,
			"concurrent and follow-up acquisitions must never claim the same job twice");
	}

	private static IFairQueueStorage GetFairStorage(IJobStorage storage, string caseName) =>
		ConformanceAssert.IsAssignableFrom<IFairQueueStorage>(
			storage,
			caseName,
			"a storage advertising fair-queue support must implement IFairQueueStorage"
		);

	private static JobAcquisitionRequest CreateRequest(
		string workerId,
		int capacity,
		FairQueuePolicy? policy,
		TimeSpan? lease = null,
		string jobName = "fair-job"
	) => new()
	{
		WorkerId = workerId,
		Lease = lease ?? TimeSpan.FromMinutes(1),
		BatchSize = capacity,
		FairQueues = policy,
		Queues =
		[
			new()
			{
				QueueName = DefaultQueueName,
				Capacity = capacity,
				JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [jobName] = capacity },
			},
		],
	};

	private static ValueTask EnqueueAsync(
		IJobStorage storage,
		FakeTimeProvider clock,
		string id,
		int order,
		string? groupId,
		CancellationToken cancellationToken,
		string jobName = "fair-job"
	) => storage.EnqueueAsync(new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = jobName,
		Payload = "{}",
		GroupId = groupId,
		State = JobState.Pending,
		DueAt = clock.GetUtcNow(),
		CreatedAt = clock.GetUtcNow().AddTicks(order),
	}, cancellationToken);

	private static JobRecord Single(IReadOnlyList<JobRecord> jobs, string caseName, string invariant)
	{
		ConformanceAssert.Equal(1, jobs.Count, caseName, invariant);
		return jobs[0];
	}

	private static string DefaultQueueName { get; } = new JobRecord
	{
		JobHandle = JobHandle.FromString("default-queue-probe"),
		JobName = "default-queue-probe",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = DateTimeOffset.UnixEpoch,
		CreatedAt = DateTimeOffset.UnixEpoch,
	}.QueueName;
}
#pragma warning restore IDE0130

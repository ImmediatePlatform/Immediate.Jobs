using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
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

	internal static IReadOnlyList<JobStorageConformanceCaseDefinition> Cases { get; } =
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
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = serviceProvider;
		cancellationToken.ThrowIfCancellationRequested();
		_ = GetFairStorage(storage, CapabilityName);
		return ValueTask.CompletedTask;
	}

	private static async ValueTask RotatesAcrossGroupsAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, RotationName);
		var clock = GetClock(serviceProvider, RotationName);
		await EnqueueAsync(storage, clock, "rotation-a-1", 0, "group-a", cancellationToken).ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "rotation-a-2", 1, "group-a", cancellationToken).ConfigureAwait(false);
		var first = Single(
			await storage.AcquireDueJobsAsync(CreateRequest("rotation-worker-a", 1, DefaultPolicy), cancellationToken)
				.ConfigureAwait(false), RotationName, "the first fair acquisition must claim one job");
		await storage.CompleteAsync(first.Id, first.Attempt, "rotation-worker-a", cancellationToken).ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "rotation-b-1", 2, "group-b", cancellationToken).ConfigureAwait(false);
		var second = Single(
			await storage.AcquireDueJobsAsync(CreateRequest("rotation-worker-b", 1, DefaultPolicy), cancellationToken)
				.ConfigureAwait(false), RotationName, "the second fair acquisition must claim one job");
		ConformanceAssert.Equal("rotation-a-1", first.Id, RotationName, "ordinary due order selects the first group initially");
		ConformanceAssert.Equal("rotation-b-1", second.Id, RotationName,
			"a newly arrived group must advance ahead of previously served backlog");

		const string ExistingJobName = "fair-existing-groups";
		await EnqueueAsync(storage, clock, "existing-a-1", 3, "existing-a", cancellationToken, ExistingJobName)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "existing-a-2", 4, "existing-a", cancellationToken, ExistingJobName)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "existing-b-1", 5, "existing-b", cancellationToken, ExistingJobName)
			.ConfigureAwait(false);
		var existingFirst = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("existing-worker-a", 1, DefaultPolicy, jobName: ExistingJobName),
				cancellationToken
			).ConfigureAwait(false),
			RotationName,
			"capacity-one fair acquisition must claim one existing-group job"
		);
		await storage.CompleteAsync(existingFirst.Id, existingFirst.Attempt, "existing-worker-a", cancellationToken)
			.ConfigureAwait(false);
		var existingSecond = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("existing-worker-b", 1, DefaultPolicy, jobName: ExistingJobName),
				cancellationToken
			).ConfigureAwait(false),
			RotationName,
			"the next fair acquisition must claim one existing-group job"
		);
		ConformanceAssert.SequenceEqual(
			["existing-a-1", "existing-b-1"],
			[existingFirst.Id, existingSecond.Id],
			RotationName,
			"capacity-one acquisitions must rotate across existing grouped backlogs"
		);

		const string ReturningJobName = "fair-returning-group";
		await EnqueueAsync(storage, clock, "returning-a-1", 6, "returning-a", cancellationToken, ReturningJobName)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "returning-a-2", 7, "returning-a", cancellationToken, ReturningJobName)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "returning-b-1", 8, "returning-b", cancellationToken, ReturningJobName)
			.ConfigureAwait(false);
		var returningFirst = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("returning-worker-a", 1, DefaultPolicy, jobName: ReturningJobName),
				cancellationToken
			).ConfigureAwait(false),
			RotationName,
			"returning-group setup must claim its first job"
		);
		await storage.CompleteAsync(returningFirst.Id, returningFirst.Attempt, "returning-worker-a", cancellationToken)
			.ConfigureAwait(false);
		var returningSecond = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("returning-worker-b", 1, DefaultPolicy, jobName: ReturningJobName),
				cancellationToken
			).ConfigureAwait(false),
			RotationName,
			"returning-group setup must rotate to the second group"
		);
		await storage.CompleteAsync(returningSecond.Id, returningSecond.Attempt, "returning-worker-b", cancellationToken)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "returning-b-2", 9, "returning-b", cancellationToken, ReturningJobName)
			.ConfigureAwait(false);
		var returned = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("returning-worker-c", 1, DefaultPolicy, jobName: ReturningJobName),
				cancellationToken
			).ConfigureAwait(false),
			RotationName,
			"a returning group must rejoin fair rotation"
		);
		ConformanceAssert.Equal(
			"returning-b-2",
			returned.Id,
			RotationName,
			"a cleared group must return without historical cursor debt"
		);
	}

	private static async ValueTask InterleavesGroupsAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, InterleaveName);
		var clock = GetClock(serviceProvider, InterleaveName);
		await EnqueueAsync(storage, clock, "interleave-a-1", 0, "group-a", cancellationToken).ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "interleave-a-2", 1, "group-a", cancellationToken).ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "interleave-b-1", 2, "group-b", cancellationToken).ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "interleave-b-2", 3, "group-b", cancellationToken).ConfigureAwait(false);
		var acquired = await storage.AcquireDueJobsAsync(
			CreateRequest("interleave-worker", 4, DefaultPolicy), cancellationToken).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual(
			["interleave-a-1", "interleave-b-1", "interleave-a-2", "interleave-b-2"],
			acquired.Select(static job => job.Id),
			InterleaveName,
			"one fair acquisition must rotate among grouped backlogs",
			comparer: StringComparer.Ordinal
		);
	}

	private static async ValueTask ServesQuietGroupAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, NoisyName);
		var clock = GetClock(serviceProvider, NoisyName);
		await EnqueueAsync(storage, clock, "noisy-active-1", 0, "noisy", cancellationToken).ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "noisy-active-2", 1, "noisy", cancellationToken).ConfigureAwait(false);
		var active = await storage.AcquireDueJobsAsync(
			CreateRequest("existing-worker", 2, policy: null), cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Equal(2, active.Count, NoisyName, "the noisy group setup must create two active jobs");
		await EnqueueAsync(storage, clock, "noisy-waiting", 2, "noisy", cancellationToken).ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "quiet-waiting", 3, "quiet", cancellationToken).ConfigureAwait(false);
		var acquired = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("quiet-worker", 1, new()
				{
					ConcurrencyShareThreshold = 0.50,
					MinInflightForNoisy = 2,
					GroupRoundRobin = true,
				}), cancellationToken).ConfigureAwait(false),
			NoisyName,
			"the noisy-neighbor acquisition must claim one job"
		);
		ConformanceAssert.Equal("quiet-waiting", acquired.Id, NoisyName,
			"a quiet group must be served before a group exceeding the in-flight threshold");
	}

	private static async ValueTask IgnoresExpiredLeasesAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, ExpiredName);
		var clock = GetClock(serviceProvider, ExpiredName);
		await EnqueueAsync(storage, clock, "expired-1", 0, "formerly-noisy", cancellationToken, "expired-setup")
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "expired-2", 1, "formerly-noisy", cancellationToken, "expired-setup")
			.ConfigureAwait(false);
		var active = await storage.AcquireDueJobsAsync(
			CreateRequest("expired-worker", 2, policy: null, lease: TimeSpan.FromSeconds(1), jobName: "expired-setup"),
			cancellationToken)
			.ConfigureAwait(false);
		ConformanceAssert.Equal(2, active.Count, ExpiredName, "the expired-lease setup must claim two jobs");
		await EnqueueAsync(storage, clock, "formerly-noisy-waiting", 2, "formerly-noisy", cancellationToken)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "quiet-after-expiry", 3, "quiet", cancellationToken).ConfigureAwait(false);
		clock.Advance(TimeSpan.FromSeconds(2));
		var acquired = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("after-expiry-worker", 1, new()
				{
					ConcurrencyShareThreshold = 0.50,
					MinInflightForNoisy = 2,
					GroupRoundRobin = true,
				}), cancellationToken).ConfigureAwait(false),
			ExpiredName,
			"the post-expiry acquisition must claim one job"
		);
		ConformanceAssert.Equal("formerly-noisy-waiting", acquired.Id, ExpiredName,
			"expired leases must not contribute to noisy-group classification");
	}

	private static async ValueTask NullPolicyPreservesOrderAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, OrdinaryName);
		var clock = GetClock(serviceProvider, OrdinaryName);
		await EnqueueAsync(storage, clock, "ordinary-a-1", 0, "group-a", cancellationToken).ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "ordinary-a-2", 1, "group-a", cancellationToken).ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "ordinary-b-1", 2, "group-b", cancellationToken).ConfigureAwait(false);
		var first = Single(
			await storage.AcquireDueJobsAsync(CreateRequest("ordinary-worker-a", 1, policy: null), cancellationToken)
				.ConfigureAwait(false), OrdinaryName, "ordinary acquisition must claim one job");
		await storage.CompleteAsync(first.Id, first.Attempt, "ordinary-worker-a", cancellationToken).ConfigureAwait(false);
		var second = Single(
			await storage.AcquireDueJobsAsync(CreateRequest("ordinary-worker-b", 1, policy: null), cancellationToken)
				.ConfigureAwait(false), OrdinaryName, "second ordinary acquisition must claim one job");
		ConformanceAssert.SequenceEqual(
			["ordinary-a-1", "ordinary-a-2"],
			[first.Id, second.Id],
			OrdinaryName,
			"a null fair policy must retain ordinary due ordering",
			comparer: StringComparer.Ordinal
		);

		const string UngroupedJobName = "fair-ungrouped";
		await EnqueueAsync(storage, clock, "ungrouped-newer", 5, groupId: null, cancellationToken, UngroupedJobName)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "ungrouped-oldest", 3, groupId: null, cancellationToken, UngroupedJobName)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "ungrouped-middle", 4, groupId: null, cancellationToken, UngroupedJobName)
			.ConfigureAwait(false);
		var ungrouped = await storage.AcquireDueJobsAsync(
			CreateRequest("ungrouped-worker", 3, DefaultPolicy, jobName: UngroupedJobName),
			cancellationToken
		).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual(
			["ungrouped-oldest", "ungrouped-middle", "ungrouped-newer"],
			ungrouped.Select(static job => job.Id),
			OrdinaryName,
			"enabling fair queues must preserve due order for ungrouped jobs"
		);

		const string NoRoundRobinJobName = "fair-no-round-robin";
		await EnqueueAsync(storage, clock, "no-rr-a-1", 6, "no-rr-a", cancellationToken, NoRoundRobinJobName)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "no-rr-a-2", 7, "no-rr-a", cancellationToken, NoRoundRobinJobName)
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "no-rr-b-1", 8, "no-rr-b", cancellationToken, NoRoundRobinJobName)
			.ConfigureAwait(false);
		var roundRobinFirst = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("no-rr-worker-a", 1, DefaultPolicy, jobName: NoRoundRobinJobName),
				cancellationToken
			).ConfigureAwait(false),
			OrdinaryName,
			"round-robin setup must claim one job"
		);
		await storage.CompleteAsync(roundRobinFirst.Id, roundRobinFirst.Attempt, "no-rr-worker-a", cancellationToken)
			.ConfigureAwait(false);
		var roundRobinDisabled = Single(
			await storage.AcquireDueJobsAsync(
				CreateRequest("no-rr-worker-b", 1, new()
				{
					ConcurrencyShareThreshold = 0.10,
					MinInflightForNoisy = 30,
					GroupRoundRobin = false,
				}, jobName: NoRoundRobinJobName),
				cancellationToken
			).ConfigureAwait(false),
			OrdinaryName,
			"disabling group round-robin must still claim one job"
		);
		ConformanceAssert.Equal(
			"no-rr-a-2",
			roundRobinDisabled.Id,
			OrdinaryName,
			"disabling group round-robin must restore ordinary due order"
		);

		await EnqueueAsync(storage, clock, "capacity-ungrouped-limited", 9, groupId: null, cancellationToken, "limited")
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "capacity-grouped-limited", 10, "capacity-a", cancellationToken, "limited")
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "capacity-other-1", 11, "capacity-b", cancellationToken, "other")
			.ConfigureAwait(false);
		await EnqueueAsync(storage, clock, "capacity-other-2", 12, "capacity-c", cancellationToken, "other")
			.ConfigureAwait(false);
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
		}, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual(
			["capacity-ungrouped-limited", "capacity-other-1"],
			capacityLimited.Select(static job => job.Id),
			OrdinaryName,
			"fair acquisition must preserve ungrouped priority while honoring queue and job capacities"
		);
	}

	private static async ValueTask ConcurrentClaimsAreDistinctAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = GetFairStorage(storage, ConcurrencyName);
		var clock = GetClock(serviceProvider, ConcurrencyName);
		for (var index = 0; index < 12; index++)
		{
			await EnqueueAsync(
				storage,
				clock,
				string.Create(CultureInfo.InvariantCulture, $"concurrent-{index:D2}"),
				index,
				string.Create(CultureInfo.InvariantCulture, $"group-{index % 3}"),
				cancellationToken
			)
				.ConfigureAwait(false);
		}

		var claims = await Task.WhenAll(
			storage.AcquireDueJobsAsync(CreateRequest("concurrent-worker-a", 12, DefaultPolicy), cancellationToken).AsTask(),
			storage.AcquireDueJobsAsync(CreateRequest("concurrent-worker-b", 12, DefaultPolicy), cancellationToken).AsTask()
		).ConfigureAwait(false);
		var ids = claims.SelectMany(static jobs => jobs).Select(static job => job.Id).ToArray();
		ConformanceAssert.Equal(12, ids.Length, ConcurrencyName,
			"concurrent fair acquisitions must collectively claim every eligible job");
		ConformanceAssert.Equal(12, ids.Distinct(StringComparer.Ordinal).Count(), ConcurrencyName,
			"concurrent fair acquisitions must never claim the same job twice");
	}

	private static IFairQueueStorage GetFairStorage(IJobStorage storage, string caseName) =>
		ConformanceAssert.IsAssignableFrom<IFairQueueStorage>(
			storage,
			caseName,
			"a storage advertising fair-queue support must implement IFairQueueStorage"
		);

	private static FakeTimeProvider GetClock(IServiceProvider serviceProvider, string caseName) =>
		ConformanceAssert.IsAssignableFrom<FakeTimeProvider>(
			serviceProvider.GetService(typeof(TimeProvider)),
			caseName,
			"time-dependent conformance cases require FakeTimeProvider registered as TimeProvider"
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
		Id = id,
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
		Id = "default-queue-probe",
		JobName = "default-queue-probe",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = DateTimeOffset.UnixEpoch,
		CreatedAt = DateTimeOffset.UnixEpoch,
	}.QueueName;
}
#pragma warning restore IDE0130

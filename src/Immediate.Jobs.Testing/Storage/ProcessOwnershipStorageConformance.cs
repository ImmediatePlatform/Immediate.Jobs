using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class ProcessOwnershipStorageConformance
{
	private const string OwnershipName = "Queue.Ownership.RetainsProcessOwnedJobsUntilExplicitCompletion";
	private const string FairName = "FairQueues.NoisyNeighbors.CountsProcessOwnedActiveJobs";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(OwnershipName, StorageCapabilities.Queue, RetainsOwnershipAsync),
		new(FairName, StorageCapabilities.Queue, CountsActiveGroupsAsync),
	];

	private static JobSubmission Job(string id, string name, DateTimeOffset now, string? group = null) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		CreatedAt = now,
		DueAt = now,
		GroupId = group,
	};

	private static JobAcquisitionRequest Request(string name, int count, string worker = "owner") => new()
	{
		WorkerId = worker,
		Lease = TimeSpan.FromSeconds(1),
		BatchSize = count,
		Queues = [new() { QueueName = "default", Capacity = count, JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [name] = count } }],
	};

	private static async ValueTask RetainsOwnershipAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		const string Name = "process-owned";
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = Name, MaxConcurrency = 1 }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		await storage.EnqueueAsync(Job("first", Name, clock.GetUtcNow()), token);
		var first = (await storage.AcquireDueJobsAsync(Request(Name, 1), token)).Single();
		ConformanceAssert.Null(first.LeaseExpiresAt, OwnershipName, "process-owned acquisition must not publish an expiry deadline");
		await storage.EnqueueAsync(Job("second", Name, clock.GetUtcNow()), token);
		clock.Advance(TimeSpan.FromDays(1));
		await storage.RenewLeaseAsync(first.JobHandle, first.Attempt, "owner", TimeSpan.FromSeconds(1), token);
		ConformanceAssert.Null((await storage.QueryJobsAsync(new() { JobHandle = first.JobHandle }, token)).Single().LeaseExpiresAt,
			OwnershipName, "renewal validates ownership without creating a lease");
		var state = await storage.GetJobAcquisitionStateAsync(Name, token);
		ConformanceAssert.Equal((1, JobAcquisitionStatus.ConcurrencyLimited), (state.ActiveCount, state.AcquisitionStatus),
			OwnershipName, "elapsed time must not release process-owned concurrency");
		ConformanceAssert.Equal(1, (await storage.GetMonitoringSnapshotAsync(token)).DefinitionStatuses.Single().ActiveCount,
			OwnershipName, "monitoring must continue to count the active job");
		ConformanceAssert.Equal(0, (await storage.AcquireDueJobsAsync(Request(Name, 1, "other"), token)).Count,
			OwnershipName, "elapsed time must neither reclaim the active job nor admit another job above its limit");
		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.RenewLeaseAsync(first.JobHandle, first.Attempt, "other", TimeSpan.FromSeconds(1), token),
			OwnershipName, "another worker cannot renew process ownership");
		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.CompleteAsync(first.JobHandle, first.Attempt + 1, "owner", token),
			OwnershipName, "an incorrect attempt cannot complete process-owned work");
		await storage.CompleteAsync(first.JobHandle, first.Attempt, "owner", token);
		ConformanceAssert.Equal(JobHandle.FromString("second"), (await storage.AcquireDueJobsAsync(Request(Name, 1, "other"), token)).Single().JobHandle,
			OwnershipName, "explicit completion must release the concurrency slot");
		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.RenewLeaseAsync(first.JobHandle, first.Attempt, "owner", TimeSpan.FromSeconds(1), token),
			OwnershipName, "a completed attempt cannot renew ownership");
	}

	private static async ValueTask CountsActiveGroupsAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = "setup" }, new() { Name = "work" }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		await storage.EnqueueAsync(Job("active-1", "setup", clock.GetUtcNow(), "noisy"), token);
		await storage.EnqueueAsync(Job("active-2", "setup", clock.GetUtcNow(), "noisy"), token);
		ConformanceAssert.Equal(2, (await storage.AcquireDueJobsAsync(Request("setup", 2), token)).Count,
			FairName, "the noisy group must own two active jobs");
		await storage.EnqueueAsync(Job("a-noisy", "work", clock.GetUtcNow(), "noisy"), token);
		await storage.EnqueueAsync(Job("b-quiet", "work", clock.GetUtcNow(), "quiet"), token);
		clock.Advance(TimeSpan.FromDays(1));
		var acquired = await storage.AcquireDueJobsAsync(Request("work", 1) with
		{
			FairQueues = new() { ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = 2, GroupRoundRobin = true },
		}, token);
		ConformanceAssert.Equal(JobHandle.FromString("b-quiet"), acquired.Single().JobHandle, FairName,
			"process-owned active jobs must still contribute to noisy-group classification after time advances");
	}
}

using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryStorageIndexTests
{
	[Fact]
	public async Task BatchAndDefinitionLookupsReflectRecoveryCancellationPurgeAndHandleReuse()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var batch = BatchHandle.FromString("batch");
		var otherBatch = BatchHandle.FromString("other-batch");
		var active = Record("active", "WORK") with { BatchHandle = batch, State = JobState.Active, WorkerId = "worker", LeaseExpiresAt = clock.GetUtcNow().AddHours(1) };
		var pending = Record("pending", "Work") with { BatchHandle = batch };
		var other = Record("other", "Other") with { BatchHandle = otherBatch };
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
	public async Task StandaloneDeleteAndPurgeRemoveDefinitionMembershipBeforeHandleReuse()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var handle = JobHandle.FromString("reused");
		await storage.EnqueueAsync(Submission(handle, "work"), token);
		await storage.EnqueueAsync(Submission(JobHandle.FromString("other"), "other"), token);
		await storage.CancelAsync(handle, token);
		await storage.DeleteAsync(handle, token);
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
	public async Task RecurringReplacementMovesDefinitionMembershipAndDedupeDoesNotAddPhantomHandles()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var original = Record("reused", "Work");
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
		var replacement = original with { JobName = "Other", RecurringKey = "occurrence" };
		var next = clock.GetUtcNow().AddMinutes(1);
		Assert.True(await storage.MaterializeRecurringAsync(schedule, replacement, next, cancellationToken: token));
		Assert.Empty(await storage.QueryJobsAsync(new() { JobName = "work" }, token));
		Assert.Equal(original.JobHandle, Assert.Single(await storage.QueryNonCompletedJobsAsync("OTHER", token)).JobHandle);
		Assert.False(await storage.MaterializeRecurringAsync(schedule with { NextRunAt = next }, replacement with { JobHandle = JobHandle.FromString("duplicate") }, next.AddMinutes(1), cancellationToken: token));
		Assert.Single(await storage.QueryJobsAsync(new() { JobName = "other" }, token));
		Assert.Empty(await storage.QueryJobsAsync(new() { JobHandle = JobHandle.FromString("duplicate") }, token));
	}

	private static ValueTask RegisterAsync(InMemoryJobStorage storage, CancellationToken token) => storage.MergeJobDefinitionsListAsync(new()
	{
		Definitions = [new() { Name = "Work", MaxConcurrency = 1 }, new() { Name = "Other" }],
		RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
	}, token);

	private static JobRecord Record(string id, string name) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		Payload = "{}",
		State = JobState.Pending,
		CreatedAt = DateTimeOffset.UnixEpoch,
		DueAt = DateTimeOffset.UnixEpoch,
	};

	private static JobSubmission Submission(JobHandle handle, string name) => new()
	{
		JobHandle = handle,
		JobName = name,
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
}

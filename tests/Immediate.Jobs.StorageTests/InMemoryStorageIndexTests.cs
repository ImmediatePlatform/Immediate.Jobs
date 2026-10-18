using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryStorageIndexTests
{
	[Fact]
	public async Task CompletionUpdatesTheStoredParentAfterInsertingManyContinuations()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var parent = Record("parent", "Work") with { State = JobState.Active, Attempt = 1, WorkerId = "worker", LeaseExpiresAt = clock.GetUtcNow().AddHours(1) };
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
		var failed = Record("failed", "Other") with { State = JobState.Failed, CompletedAt = DateTimeOffset.UnixEpoch };
		var first = Record("first", "Work") with { BatchHandle = batch, State = JobState.WaitingForTrigger };
		var second = Record("second", "Work") with { BatchHandle = batch, State = JobState.WaitingForTrigger, RemainingDependencies = 1 };
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
	public async Task RecurringMaterializationIndexesNewJobsAndDedupeDoesNotAddPhantomHandles()
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		await using var storage = new InMemoryJobStorage(clock);
		await RegisterAsync(storage, token);
		var original = Record("existing", "Work");
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
		var materialized = Record("recurring", "Other") with { RecurringKey = "occurrence" };
		var next = clock.GetUtcNow().AddMinutes(1);
		Assert.True(await storage.MaterializeRecurringAsync(schedule, materialized, next, cancellationToken: token));
		Assert.Equal(original.JobHandle, Assert.Single(await storage.QueryJobsAsync(new() { JobName = "work" }, token)).JobHandle);
		Assert.Equal(materialized.JobHandle, Assert.Single(await storage.QueryNonCompletedJobsAsync("OTHER", token)).JobHandle);
		Assert.False(await storage.MaterializeRecurringAsync(schedule with { NextRunAt = next }, materialized with { JobHandle = JobHandle.FromString("duplicate") }, next.AddMinutes(1), cancellationToken: token));
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

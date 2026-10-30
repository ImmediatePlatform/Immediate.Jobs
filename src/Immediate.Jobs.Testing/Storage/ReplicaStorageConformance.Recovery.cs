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
	private const string InterruptedRecoveryName = "Replica.Recovery.InterruptsOwnershipBeforeLoadingWorkflowSnapshot";
	private const string PagedRecoveryName = "Replica.Recovery.RestoresAllPagesAndPreservesSettledProgress";

	private static PersistedJobState PagedRecoveryState() => new()
	{
		Jobs = [.. Enumerable.Range(0, 1001).Select(index =>
		{
			var state = (index % 5) switch
			{
				0 => JobState.Pending,
				1 => JobState.Scheduled,
				2 => JobState.Succeeded,
				3 => JobState.Cancelled,
				_ => JobState.WaitingForTrigger,
			};
			var createdAt = DateTimeOffset.UnixEpoch.AddMilliseconds(index);
			return Job(string.Create(CultureInfo.InvariantCulture, $"paged-{index:D4}"), createdAt).ToJobRecord(JobName) with
			{
				State = state,
				CompletedAt = state is JobState.Succeeded or JobState.Cancelled ? createdAt : null,
			};
		})],
		Batches = [],
		Edges = [],
		RecurringSchedules = [],
	};

	private static async ValueTask RestoresPagesAndSettledProgressAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = JobName, QueueName = QueueName, Tags = ["recovery"] }],
			ServerTags = ["recovery"],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		var graph = Replica(storage, PagedRecoveryName);
		var now = clock.GetUtcNow();
		var batch = BatchHandle.FromString("paged-workflow");
		var failed = Job("paged-failed", now) with { BatchHandle = batch };
		var pending = Job("paged-pending", DateTimeOffset.UnixEpoch) with { BatchHandle = batch };
		var child = Job("paged-child", now) with { BatchHandle = batch };
		JobContinuationEdge[] edges =
		[
			new() { ParentJobHandle = failed.JobHandle, ChildJobHandle = child.JobHandle, Trigger = ContinuationTrigger.Complete, Delay = TimeSpan.FromHours(2) },
			new() { ParentJobHandle = pending.JobHandle, ChildJobHandle = child.JobHandle, Trigger = ContinuationTrigger.Complete, Delay = TimeSpan.Zero },
		];
		await graph.EnqueueBatchAsync(new() { BatchHandle = batch, CreatedAt = now, State = BatchState.Executing, TotalJobs = 3, PendingCount = 3 },
			[failed, pending, child], edges, token);
		var acquired = (await graph.AcquireJobsAsync([failed.JobHandle], "previous-process", TimeSpan.FromMinutes(1), token)).Single();
		await storage.FailAsync(failed.JobHandle, acquired.Attempt, "previous-process", "previous failure", nextRetryAt: null, token);
		var childBefore = await GetJobAsync(storage, child.JobHandle, PagedRecoveryName, token);
		var batchBefore = await graph.GetBatchStatusAsync(batch, token);

		await using var provider = CreateSingleServerProvider(storage, clock);
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(interruptActiveLeases: false, token);
		var recovered = (await writer.QueryJobsAsync(new() { Take = 1000 }, token))
			.Concat(await writer.QueryJobsAsync(new() { Skip = 1000, Take = 1000 }, token)).ToList();
		var expectedHandles = PagedRecoveryState().Jobs.Select(static job => job.JobHandle.Value)
			.Concat([failed.JobHandle.Value, pending.JobHandle.Value, child.JobHandle.Value]);
		ConformanceAssert.SequenceEqual(expectedHandles.Order(StringComparer.Ordinal), recovered.Select(static job => job.JobHandle.Value).Order(StringComparer.Ordinal),
			PagedRecoveryName, "startup must restore every job exactly once across mixed-state page boundaries");
		ConformanceAssert.True(recovered.All(job => string.Equals(job.QueueName, QueueName, StringComparison.Ordinal)),
			PagedRecoveryName, "restored definitions must resolve queues for every recovered invocation");
		ConformanceAssert.Equal(childBefore, await GetJobAsync(writer, child.JobHandle, PagedRecoveryName, token),
			PagedRecoveryName, "loading must preserve partially settled dependency counts and delay metadata");
		ConformanceAssert.Equal(batchBefore, await Replica(writer, PagedRecoveryName).GetBatchStatusAsync(batch, token),
			PagedRecoveryName, "loading must preserve persisted batch aggregates");
		ConformanceAssert.Equal(2, (await Replica(writer, PagedRecoveryName).GetBatchGraphAsync(batch, token))!.Edges.Count,
			PagedRecoveryName, "startup must restore incoming edges for recovered batch members");

		await writer.RetryAsync(failed.JobHandle, token);
		await writer.CancelAsync(failed.JobHandle, token);
		ConformanceAssert.Equal(childBefore, await GetJobAsync(writer, child.JobHandle, PagedRecoveryName, token),
			PagedRecoveryName, "a previously settled edge must not be applied again after retrying its parent");
	}

	private static async ValueTask InterruptsBeforeSnapshotAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var graph = Replica(storage, InterruptedRecoveryName);
		var now = clock.GetUtcNow();
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = JobName, QueueName = QueueName }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		var batch = BatchHandle.FromString("interrupted-batch");
		var parent = Job("interrupted-parent", now) with { BatchHandle = batch };
		var child = Job("waiting-child", now) with { BatchHandle = batch };
		await graph.EnqueueBatchAsync(new() { BatchHandle = batch, CreatedAt = now, State = BatchState.Executing, TotalJobs = 2, PendingCount = 2 },
			[parent, child], [new() { ParentJobHandle = parent.JobHandle, ChildJobHandle = child.JobHandle, Delay = TimeSpan.Zero }], token);
		var first = (await graph.AcquireJobsAsync([parent.JobHandle], "previous-process", TimeSpan.FromHours(1), token)).Single();
		var services = new ServiceCollection();
		services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
		services.AddSingleton<TimeProvider>(clock);
		services.AddImmediateJobsCore().ConfigureStorage(builder => builder.UseSingleServer(_ => storage));
		await using var provider = services.BuildServiceProvider();
		var writer = provider.GetRequiredService<IJobStorage>();
		await writer.InitializeAsync(interruptActiveLeases: false, token);
		var restored = (await writer.QueryJobsAsync(new() { JobHandle = parent.JobHandle }, token)).Single();
		ConformanceAssert.Equal(JobState.Pending, restored.State, InterruptedRecoveryName, "the mirror receives already interrupted ownership");
		ConformanceAssert.Equal(first.Attempt, restored.Attempt, InterruptedRecoveryName, "startup preserves the execution ordinal");
		var waiting = (await writer.QueryJobsAsync(new() { JobHandle = child.JobHandle }, token)).Single();
		ConformanceAssert.Equal(JobState.AwaitingContinuation, waiting.State, InterruptedRecoveryName, "interruption does not settle continuations");
		ConformanceAssert.Equal(1, waiting.RemainingDependencies, InterruptedRecoveryName, "dependency progress is unchanged");
		var status = (await graph.QueryBatchesAsync(new(), token)).Single();
		ConformanceAssert.Equal(2, status.Remaining, InterruptedRecoveryName, "interruption is not terminal batch progress");
		ConformanceAssert.Equal(BatchState.Executing, status.State, InterruptedRecoveryName, "the batch remains executing");
		var history = (await storage.QueryJobExecutionsAsync(parent.JobHandle, new(), token)).Single();
		ConformanceAssert.Equal(JobExecutionState.Interrupted, history.State, InterruptedRecoveryName, "durable history is interrupted before the snapshot is loaded");
	}
}

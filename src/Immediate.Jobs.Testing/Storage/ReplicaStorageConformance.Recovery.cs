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

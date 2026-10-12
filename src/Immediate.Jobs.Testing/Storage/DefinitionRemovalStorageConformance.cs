using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class DefinitionRemovalStorageConformance
{
	private const string CancellationCase = "Definitions.Removal.CancelsScopedJobsAndSchedulesAndFencesWorkers";
	private const string GraphCase = "Definitions.Removal.SettlesCancelledDependenciesAndBatchCounts";

	internal static IReadOnlyList<JobStorageConformanceTestCase> QueueCases { get; } =
	[
		new(CancellationCase, StorageCapabilities.Queue, CancelsRemovedAsync),
	];

	internal static IReadOnlyList<JobStorageConformanceTestCase> GraphCases { get; } =
	[
		new(GraphCase, StorageCapabilities.Graph, SettlesGraphAsync),
	];

	private static JobRecord Job(string id, string name, DateTimeOffset now) => new()
	{
		JobHandle = JobHandle.FromString(id),
		JobName = name,
		QueueName = "default",
		Payload = "{}",
		State = JobState.Pending,
		CreatedAt = now,
		DueAt = now,
	};

	private static JobAcquisitionRequest Request(string name) => new()
	{
		WorkerId = "worker",
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = 1,
		Queues = [new() { QueueName = "default", Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [name] = 1 } }],
	};

	private static async ValueTask CancelsRemovedAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var now = clock.GetUtcNow();
		var definition = new JobDefinitionRecord { Name = "Résumé", Tags = ["alpha"] };
		var other = new JobDefinitionRecord { Name = "Other", Tags = ["beta"] };
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [definition, other], ServerTags = ["alpha", "beta"] }, token);
		await storage.EnqueueAsync(Job("history", definition.Name, now), token);
		var history = (await storage.AcquireDueJobsAsync(Request(definition.Name), token)).Single();
		await storage.CompleteAsync(history.JobHandle, history.Attempt, "worker", token);
		await storage.EnqueueAsync(Job("active", definition.Name, now), token);
		await storage.EnqueueAsync(Job("scheduled", definition.Name, now) with { State = JobState.Scheduled, DueAt = now.AddHours(1) }, token);
		await storage.EnqueueAsync(Job("waiting", definition.Name, now) with { State = JobState.WaitingForTrigger }, token);
		await storage.EnqueueAsync(Job("other", other.Name, now), token);
		var schedule = new RecurringJobSchedule
		{
			Name = "dynamic",
			JobName = "RÉSUMÉ",
			QueueName = "default",
			Cron = "* * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = now,
		};
		await storage.UpsertRecurringAsync(schedule, token);
		var acquired = (await storage.AcquireDueJobsAsync(Request(definition.Name), token)).Single();
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [], ServerTags = ["alpha"] }, token);
		foreach (var id in new[] { "active", "scheduled", "waiting" })
		{
			var job = (await storage.QueryJobsAsync(new() { JobHandle = JobHandle.FromString(id) }, token)).Single();
			ConformanceAssert.Equal(JobState.Cancelled, job.State, CancellationCase, "all nonterminal states must be canceled");
			ConformanceAssert.Equal(JobDefinitionReconciliation.RemovalCancellationReason, job.CancellationReason, CancellationCase, "the persisted reason explains the removed definition");
			ConformanceAssert.Equal<DateTimeOffset?>(now, job.CompletedAt, CancellationCase, "cancellation receives a completion time for retention");
			ConformanceAssert.Equal<string?>(expected: null, job.WorkerId, CancellationCase, "the previous worker loses ownership");
		}

		ConformanceAssert.Equal(JobState.Succeeded, (await storage.GetJobStatusAsync(JobHandle.FromString("history"), token))!.State, CancellationCase, "terminal history remains intact");
		ConformanceAssert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(JobHandle.FromString("other"), token))!.State, CancellationCase, "unrelated tag scopes remain intact");
		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteAsync(acquired.JobHandle, acquired.Attempt, "worker", token), CancellationCase, "stale completion cannot replace cancellation");
		var cancelled = (await storage.GetMonitoringSnapshotAsync(token)).Recurring.Single();
		ConformanceAssert.Equal(JobDefinitionReconciliation.RemovalCancellationReason, cancelled.CancellationReason, CancellationCase, "dynamic schedules remain visible with their cancellation reason");
		ConformanceAssert.True(cancelled.IsPaused, CancellationCase, "canceled schedules cannot emit occurrences");
		ConformanceAssert.Equal(expected: 0, (await storage.GetDueRecurringAsync(now.AddHours(1), 10, [definition.Name], token)).Count, CancellationCase, "due queries exclude canceled schedules");
		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(() => storage.ResumeRecurringAsync(schedule.Name, token), CancellationCase, "ordinary resume cannot restart a canceled schedule");
		// A scheduler may have read this original snapshot before reconciliation canceled it.
		await storage.UpsertRecurringAsync(schedule with { IsPaused = false, NextRunAt = now.AddMinutes(2) }, token);
		var updated = (await storage.GetMonitoringSnapshotAsync(token)).Recurring.Single();
		ConformanceAssert.Equal(cancelled.CancellationReason, updated.CancellationReason, CancellationCase, "a stale upsert preserves cancellation");
		ConformanceAssert.True(updated.IsPaused, CancellationCase, "a stale upsert cannot reactivate a canceled schedule");
		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(() => storage.ResumeRecurringAsync(schedule.Name, token), CancellationCase, "resume remains rejected after a stale upsert");
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [], ServerTags = ["alpha"] }, token);
		ConformanceAssert.Equal(updated, (await storage.GetMonitoringSnapshotAsync(token)).Recurring.Single(), CancellationCase, "repeated reconciliation retains cancellation history");
		await storage.RemoveRecurringAsync(schedule.Name, token);
		await storage.UpsertRecurringAsync(schedule, token);
		var recreated = (await storage.GetMonitoringSnapshotAsync(token)).Recurring.Single();
		ConformanceAssert.Equal<string?>(expected: null, recreated.CancellationReason, CancellationCase, "explicit deletion and recreation creates a new schedule");
	}

	private static async ValueTask SettlesGraphAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var graph = (IJobGraphStorage)storage;
		var now = clock.GetUtcNow();
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [new() { Name = "Removed" }, new() { Name = "Kept" }] }, token);
		var batch = BatchHandle.FromString("cancel-batch");
		var parent = Job("parent", "Removed", now) with { BatchHandle = batch };
		var removedChild = Job("removed-child", "Removed", now) with { BatchHandle = batch, State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		var keptChild = Job("kept-child", "Kept", now) with { BatchHandle = batch, State = JobState.AwaitingContinuation, RemainingDependencies = 1 };
		await graph.EnqueueBatchAsync(new() { BatchHandle = batch, CreatedAt = now, State = BatchState.Executing, TotalJobs = 3, PendingCount = 3 },
			[parent, removedChild, keptChild],
			[new() { ChildJobHandle = removedChild.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero, Trigger = ContinuationTrigger.Success },
				new() { ChildJobHandle = keptChild.JobHandle, ParentJobHandle = parent.JobHandle, Delay = TimeSpan.Zero, Trigger = ContinuationTrigger.Complete }], token);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [new() { Name = "Kept" }] }, token);
		ConformanceAssert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(removedChild.JobHandle, token))!.State, GraphCase, "a removed child is canceled before dependency propagation");
		ConformanceAssert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(keptChild.JobHandle, token))!.State, GraphCase, "completion-triggered work for retained definitions is released");
		var status = (await graph.GetBatchStatusAsync(batch, token))!;
		ConformanceAssert.Equal(2, status.Cancelled, GraphCase, "batch cancellation counts include both removed members");
		ConformanceAssert.Equal(1, status.Remaining, GraphCase, "the surviving batch member remains pending");
	}
}

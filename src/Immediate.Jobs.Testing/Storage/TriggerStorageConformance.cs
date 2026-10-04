using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class TriggerStorageConformance
{
	private const string ParametersName = "Trigger.Parameters.UpdatesOnlyWaitingJobs";
	private const string ReleaseName = "Trigger.Release.ReleasesWaitingJobOnce";
	private const string FutureName = "Trigger.Release.SchedulesFutureDueTime";
	private const string ContentionName = "Trigger.Release.ReleasesOnceUnderContention";
	private const string QueryName = "Trigger.Queries.FiltersByCreationTime";
	private const string ParentsName = "Trigger.Graph.WaitsForParentsAndTrigger";
	private const string SkipName = "Trigger.Graph.SkipsWhenParentConditionFailsWhileWaiting";
	private const string BatchName = "Trigger.Batch.ReleasesEveryMemberTogether";
	private const string BatchCancellationName = "Trigger.Batch.CancelsWaitingBatch";
	private const string JobName = "conformance-trigger-job";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(ParametersName, StorageCapabilities.Queue, UpdatesOnlyWaitingJobsAsync),
		new(ReleaseName, StorageCapabilities.Queue, ReleasesWaitingJobOnceAsync),
		new(FutureName, StorageCapabilities.Queue, SchedulesFutureDueTimeAsync),
		new(ContentionName, StorageCapabilities.Queue, ReleasesOnceUnderContentionAsync),
		new(QueryName, StorageCapabilities.Queue, FiltersByCreationTimeAsync),
		new(ParentsName, StorageCapabilities.Graph, WaitsForParentsAndTriggerAsync),
		new(SkipName, StorageCapabilities.Graph, SkipsWhenParentConditionFailsAsync),
		new(BatchName, StorageCapabilities.Graph, ReleasesEveryBatchMemberAsync),
		new(BatchCancellationName, StorageCapabilities.Graph, CancelsWaitingBatchAsync),
	];

	private static async ValueTask UpdatesOnlyWaitingJobsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		var job = CreateWaitingJob("parameters", now) with { Payload = "" };
		await storage.EnqueueAsync(job, cancellationToken);

		_ = await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => storage.UpdatePayloadAsync(JobHandle.FromString("parameters-missing"), JobName, "{}", cancellationToken),
			ParametersName,
			"updating a missing job must throw KeyNotFoundException"
		);
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.UpdatePayloadAsync(job.JobHandle, "other-job", "{}", cancellationToken),
			ParametersName,
			"updating a job through another job name must throw"
		);
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			async () => _ = await storage.TryTriggerAsync(job.JobHandle, JobName, now, cancellationToken),
			ParametersName,
			"triggering a waiting job without parameters must throw"
		);

		await storage.UpdatePayloadAsync(job.JobHandle, JobName, """{"value":1}""", cancellationToken);
		await storage.UpdatePayloadAsync(job.JobHandle, JobName, """{"value":2}""", cancellationToken);
		var updated = await GetJobAsync(storage, job.JobHandle, ParametersName, cancellationToken);
		ConformanceAssert.Equal("""{"value":2}""", updated.Payload, ParametersName,
			"repeated updates must keep the latest payload");
		ConformanceAssert.Equal(JobState.WaitingForTrigger, updated.State, ParametersName,
			"updating parameters must not release the job");

		ConformanceAssert.True(
			await storage.TryTriggerAsync(job.JobHandle, JobName, now, cancellationToken),
			ParametersName,
			"a waiting job with parameters must be released"
		);
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.UpdatePayloadAsync(job.JobHandle, JobName, """{"value":3}""", cancellationToken),
			ParametersName,
			"updating a released job must throw"
		);
		ConformanceAssert.Equal(
			"""{"value":2}""",
			(await GetJobAsync(storage, job.JobHandle, ParametersName, cancellationToken)).Payload,
			ParametersName,
			"a rejected update must leave the payload unchanged"
		);
	}

	private static async ValueTask ReleasesWaitingJobOnceAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		var job = CreateWaitingJob("release", now);
		var cancelled = CreateWaitingJob("release-cancelled", now);
		await storage.EnqueueAsync(job, cancellationToken);
		await storage.EnqueueAsync(cancelled, cancellationToken);

		ConformanceAssert.Equal(
			0,
			(await storage.AcquireDueJobsAsync(CreateRequest("release-worker-a", 2), cancellationToken)).Count,
			ReleaseName,
			"a waiting job must never be acquired"
		);

		_ = await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			async () => _ = await storage.TryTriggerAsync(JobHandle.FromString("release-missing"), JobName, now, cancellationToken),
			ReleaseName,
			"triggering a missing job must throw KeyNotFoundException"
		);
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			async () => _ = await storage.TryTriggerAsync(job.JobHandle, "other-job", now, cancellationToken),
			ReleaseName,
			"triggering a job through another job name must throw"
		);

		ConformanceAssert.True(
			await storage.TryTriggerAsync(job.JobHandle, JobName, now, cancellationToken),
			ReleaseName,
			"the first trigger must release the waiting job"
		);
		ConformanceAssert.Equal(
			JobState.Pending,
			(await GetJobAsync(storage, job.JobHandle, ReleaseName, cancellationToken)).State,
			ReleaseName,
			"a job triggered for now must become pending"
		);
		ConformanceAssert.False(
			await storage.TryTriggerAsync(job.JobHandle, JobName, now, cancellationToken),
			ReleaseName,
			"a second trigger must report that the job is no longer waiting"
		);

		await storage.CancelAsync(cancelled.JobHandle, cancellationToken);
		ConformanceAssert.False(
			await storage.TryTriggerAsync(cancelled.JobHandle, JobName, now, cancellationToken),
			ReleaseName,
			"a cancelled waiting job must not be released"
		);

		var acquired = await storage.AcquireDueJobsAsync(CreateRequest("release-worker-b", 2), cancellationToken);
		ConformanceAssert.SequenceEqual(
			[job.JobHandle.Value],
			acquired.Select(static item => item.JobHandle.Value),
			ReleaseName,
			"only the released job may be acquired"
		);
		ConformanceAssert.Equal(job.Payload, acquired[0].Payload, ReleaseName,
			"the released job must run with its stored payload");
	}

	private static async ValueTask SchedulesFutureDueTimeAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		var job = CreateWaitingJob("future", now);
		await storage.EnqueueAsync(job, cancellationToken);

		var dueAt = now.AddHours(1);
		ConformanceAssert.True(
			await storage.TryTriggerAsync(job.JobHandle, JobName, dueAt, cancellationToken),
			FutureName,
			"a waiting job must be released with a future due time"
		);
		var scheduled = await GetJobAsync(storage, job.JobHandle, FutureName, cancellationToken);
		ConformanceAssert.Equal(JobState.Scheduled, scheduled.State, FutureName,
			"a job triggered for a future time must become scheduled");
		ConformanceAssert.Equal(dueAt, scheduled.DueAt, FutureName,
			"the trigger must set the due time");
		ConformanceAssert.Equal(
			0,
			(await storage.AcquireDueJobsAsync(CreateRequest("future-worker-a", 1), cancellationToken)).Count,
			FutureName,
			"a job triggered for a future time must not be acquired early"
		);

		timeProvider.Advance(TimeSpan.FromHours(1));
		var acquired = await storage.AcquireDueJobsAsync(CreateRequest("future-worker-b", 1), cancellationToken);
		ConformanceAssert.SequenceEqual(
			[job.JobHandle.Value],
			acquired.Select(static item => item.JobHandle.Value),
			FutureName,
			"a triggered job must be acquired once it is due"
		);
	}

	private static async ValueTask ReleasesOnceUnderContentionAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		var job = CreateWaitingJob("contention", now);
		await storage.EnqueueAsync(job, cancellationToken);

		var results = await Task.WhenAll(
			Enumerable.Range(0, 8)
				.Select(_ => storage.TryTriggerAsync(job.JobHandle, JobName, now, cancellationToken).AsTask())
		);

		ConformanceAssert.Equal(1, results.Count(static result => result), ContentionName,
			"concurrent triggers must release a waiting job exactly once");
	}

	private static async ValueTask FiltersByCreationTimeAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateWaitingJob("query-old", now.AddMinutes(-10)), cancellationToken);
		await storage.EnqueueAsync(CreateWaitingJob("query-new", now), cancellationToken);

		var stale = await storage.QueryJobsAsync(
			new()
			{
				State = JobState.WaitingForTrigger,
				CreatedBefore = now.AddMinutes(-5),
			},
			cancellationToken
		);

		ConformanceAssert.SequenceEqual(
			["query-old"],
			stale.Select(static job => job.JobHandle.Value),
			QueryName,
			"CreatedBefore must only match jobs created before the supplied time"
		);
	}

	private static async ValueTask WaitsForParentsAndTriggerAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, ParentsName);
		var now = timeProvider.GetUtcNow();

		var settledFirstParent = CreateJob("parents-settled-first-parent", now);
		var settledFirstChild = CreateWaitingJob("parents-settled-first-child", now) with { RemainingDependencies = 1 };
		await graph.EnqueueAsync(settledFirstParent, cancellationToken);
		await graph.EnqueueContinuationAsync(
			settledFirstChild,
			[CreateEdge(settledFirstChild, settledFirstParent)],
			cancellationToken
		);
		await AcquireAndCompleteAsync(graph, settledFirstParent, "parents-worker-a", cancellationToken);

		var settled = await GetJobAsync(graph, settledFirstChild.JobHandle, ParentsName, cancellationToken);
		ConformanceAssert.Equal(JobState.WaitingForTrigger, settled.State, ParentsName,
			"a settled parent must not release a job that is waiting for a trigger");
		ConformanceAssert.Equal(0, settled.RemainingDependencies, ParentsName,
			"settling the parent must still update the dependency counter");
		ConformanceAssert.True(
			await graph.TryTriggerAsync(settledFirstChild.JobHandle, JobName, now, cancellationToken),
			ParentsName,
			"a waiting job whose parents settled must be releasable"
		);
		ConformanceAssert.Equal(
			JobState.Pending,
			(await GetJobAsync(graph, settledFirstChild.JobHandle, ParentsName, cancellationToken)).State,
			ParentsName,
			"a triggered job without remaining dependencies must become pending"
		);
		await AcquireAndCompleteAsync(graph, settledFirstChild, "parents-worker-b", cancellationToken);

		var triggeredFirstParent = CreateJob("parents-triggered-first-parent", now);
		var triggeredFirstChild = CreateWaitingJob("parents-triggered-first-child", now) with { RemainingDependencies = 1 };
		await graph.EnqueueAsync(triggeredFirstParent, cancellationToken);
		await graph.EnqueueContinuationAsync(
			triggeredFirstChild,
			[CreateEdge(triggeredFirstChild, triggeredFirstParent)],
			cancellationToken
		);
		ConformanceAssert.True(
			await graph.TryTriggerAsync(triggeredFirstChild.JobHandle, JobName, now, cancellationToken),
			ParentsName,
			"a waiting job with unsettled parents must be releasable"
		);
		ConformanceAssert.Equal(
			JobState.AwaitingContinuation,
			(await GetJobAsync(graph, triggeredFirstChild.JobHandle, ParentsName, cancellationToken)).State,
			ParentsName,
			"a triggered job with remaining dependencies must await its parents"
		);
		await AcquireAndCompleteAsync(graph, triggeredFirstParent, "parents-worker-c", cancellationToken);
		ConformanceAssert.Equal(
			JobState.Pending,
			(await GetJobAsync(graph, triggeredFirstChild.JobHandle, ParentsName, cancellationToken)).State,
			ParentsName,
			"settling the last parent of a triggered job must release it"
		);
	}

	private static async ValueTask SkipsWhenParentConditionFailsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, SkipName);
		var now = timeProvider.GetUtcNow();
		var parent = CreateJob("skip-parent", now);
		var child = CreateWaitingJob("skip-child", now) with { RemainingDependencies = 1 };
		await graph.EnqueueAsync(parent, cancellationToken);
		await graph.EnqueueContinuationAsync(child, [CreateEdge(child, parent)], cancellationToken);

		var acquired = (await graph.AcquireDueJobsAsync(CreateRequest("skip-worker", 1), cancellationToken)).Single();
		await graph.FailAsync(acquired.JobHandle, acquired.Attempt, "skip-worker", "expected failure", nextRetryAt: null, cancellationToken);

		ConformanceAssert.Equal(
			JobState.Skipped,
			(await GetJobAsync(graph, child.JobHandle, SkipName, cancellationToken)).State,
			SkipName,
			"a waiting job whose success condition fails must be skipped"
		);
		ConformanceAssert.False(
			await graph.TryTriggerAsync(child.JobHandle, JobName, now, cancellationToken),
			SkipName,
			"a skipped job must not be released"
		);
	}

	private static async ValueTask ReleasesEveryBatchMemberAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, BatchName);
		var now = timeProvider.GetUtcNow();
		var batchHandle = BatchHandle.FromString("trigger-batch");
		var root = CreateWaitingJob("trigger-batch-root", now) with { BatchHandle = batchHandle };
		var child = CreateWaitingJob("trigger-batch-child", now) with { BatchHandle = batchHandle, RemainingDependencies = 1 };
		await graph.EnqueueBatchAsync(
			CreateWaitingBatch(batchHandle, now, 2),
			[root, child],
			[CreateEdge(child, root)],
			cancellationToken
		);

		ConformanceAssert.Equal(
			BatchState.WaitingForTrigger,
			ConformanceAssert.NotNull(await graph.GetBatchStatusAsync(batchHandle, cancellationToken), BatchName,
				"the waiting batch must exist").State,
			BatchName,
			"a batch committed waiting for a trigger must keep that state"
		);
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			async () => _ = await graph.TryTriggerAsync(root.JobHandle, JobName, now, cancellationToken),
			BatchName,
			"triggering a member of a waiting batch individually must throw"
		);
		ConformanceAssert.Equal(
			0,
			(await graph.AcquireDueJobsAsync(CreateRequest("batch-worker-a", 2), cancellationToken)).Count,
			BatchName,
			"members of a waiting batch must never be acquired"
		);

		ConformanceAssert.True(
			await graph.TryTriggerBatchAsync(batchHandle, cancellationToken),
			BatchName,
			"the first batch trigger must release the batch"
		);
		ConformanceAssert.Equal(
			BatchState.Executing,
			(await graph.GetBatchStatusAsync(batchHandle, cancellationToken))?.State,
			BatchName,
			"a triggered batch must be executing"
		);
		ConformanceAssert.Equal(
			JobState.Pending,
			(await GetJobAsync(graph, root.JobHandle, BatchName, cancellationToken)).State,
			BatchName,
			"a triggered root member must become pending"
		);
		ConformanceAssert.Equal(
			JobState.AwaitingContinuation,
			(await GetJobAsync(graph, child.JobHandle, BatchName, cancellationToken)).State,
			BatchName,
			"a triggered member with remaining dependencies must await its parents"
		);
		ConformanceAssert.False(
			await graph.TryTriggerBatchAsync(batchHandle, cancellationToken),
			BatchName,
			"a second batch trigger must report that the batch is no longer waiting"
		);
		ConformanceAssert.False(
			await graph.TryTriggerAsync(root.JobHandle, JobName, now, cancellationToken),
			BatchName,
			"triggering a member of a released batch must report that the member is no longer waiting"
		);
		_ = await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			async () => _ = await graph.TryTriggerBatchAsync(BatchHandle.FromString("trigger-batch-missing"), cancellationToken),
			BatchName,
			"triggering a missing batch must throw KeyNotFoundException"
		);

		var acquired = await graph.AcquireDueJobsAsync(CreateRequest("batch-worker-b", 2), cancellationToken);
		ConformanceAssert.SequenceEqual(
			[root.JobHandle.Value],
			acquired.Select(static job => job.JobHandle.Value),
			BatchName,
			"only the released root member may be acquired"
		);
	}

	private static async ValueTask CancelsWaitingBatchAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var graph = GetGraph(storage, BatchCancellationName);
		var now = timeProvider.GetUtcNow();
		var batchHandle = BatchHandle.FromString("cancel-trigger-batch");
		var member = CreateWaitingJob("cancel-trigger-batch-member", now) with { BatchHandle = batchHandle };
		await graph.EnqueueBatchAsync(CreateWaitingBatch(batchHandle, now, 1), [member], [], cancellationToken);

		await graph.CancelBatchAsync(batchHandle, cancellationToken);

		ConformanceAssert.Equal(
			BatchState.Cancelled,
			(await graph.GetBatchStatusAsync(batchHandle, cancellationToken))?.State,
			BatchCancellationName,
			"cancelling a waiting batch must cancel it"
		);
		ConformanceAssert.Equal(
			JobState.Cancelled,
			(await GetJobAsync(graph, member.JobHandle, BatchCancellationName, cancellationToken)).State,
			BatchCancellationName,
			"cancelling a waiting batch must cancel its members"
		);
		ConformanceAssert.False(
			await graph.TryTriggerBatchAsync(batchHandle, cancellationToken),
			BatchCancellationName,
			"a cancelled batch must not be released"
		);
	}

	private static async ValueTask AcquireAndCompleteAsync(
		IJobStorage storage,
		JobRecord job,
		string workerId,
		CancellationToken cancellationToken
	)
	{
		var acquired = (await storage.AcquireDueJobsAsync(CreateRequest(workerId, 1), cancellationToken)).Single();
		ConformanceAssert.Equal(job.JobHandle, acquired.JobHandle, ParentsName, "the next due job must be acquired");
		await storage.CompleteAsync(acquired.JobHandle, acquired.Attempt, workerId, cancellationToken);
	}

	private static IJobGraphStorage GetGraph(IJobStorage storage, string caseName) =>
		ConformanceAssert.IsAssignableFrom<IJobGraphStorage>(
			storage,
			caseName,
			"a storage advertising graph support must implement IJobGraphStorage"
		);

	private static JobRecord CreateJob(string id, DateTimeOffset createdAt) =>
		new()
		{
			JobHandle = JobHandle.FromString(id),
			JobName = JobName,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = createdAt,
			CreatedAt = createdAt,
		};

	private static JobRecord CreateWaitingJob(string id, DateTimeOffset createdAt) =>
		CreateJob(id, createdAt) with
		{
			Payload = string.Create(CultureInfo.InvariantCulture, $$"""{"id":"{{id}}"}"""),
			State = JobState.WaitingForTrigger,
		};

	private static JobContinuationEdge CreateEdge(JobRecord child, JobRecord parent) =>
		new()
		{
			ChildJobHandle = child.JobHandle,
			ParentJobHandle = parent.JobHandle,
			Delay = TimeSpan.Zero,
		};

	private static BatchRecord CreateWaitingBatch(BatchHandle id, DateTimeOffset createdAt, int count) =>
		new()
		{
			BatchHandle = id,
			CreatedAt = createdAt,
			TotalJobs = count,
			PendingCount = count,
			State = BatchState.WaitingForTrigger,
		};

	private static JobAcquisitionRequest CreateRequest(string workerId, int batchSize) =>
		new()
		{
			WorkerId = workerId,
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = batchSize,
			Queues =
			[
				new()
				{
					QueueName = "default",
					Capacity = batchSize,
					JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [JobName] = batchSize },
				},
			],
		};

	private static async ValueTask<JobRecord> GetJobAsync(
		IJobStorage storage,
		JobHandle id,
		string caseName,
		CancellationToken cancellationToken
	) => ConformanceAssert.NotNull(
		(await storage.QueryJobsAsync(new() { JobHandle = id }, cancellationToken)).SingleOrDefault(),
		caseName,
		"the expected job must exist",
		$"jobHandle={id.Value}"
	);
}

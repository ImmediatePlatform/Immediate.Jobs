using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class InitializationStorageConformance
{
	private const string Name = "Queue.Initialization.InterruptsAbandonedExecutionsWithoutResettingLimits";
	private const string LegacyName = "Queue.Initialization.InterruptsLegacyExecution";
	private const string JobName = "startup-job";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(Name, StorageCapabilities.Queue, InterruptAsync),
		new(LegacyName, StorageCapabilities.Graph, InterruptLegacyAsync, new()
		{
			Jobs = [new()
			{
				JobHandle = JobHandle.FromString("legacy-startup"), JobName = JobName, Payload = "{}",
				State = JobState.Active, Attempt = 3, WorkerId = "old-worker",
				CreatedAt = DateTimeOffset.UnixEpoch, DueAt = DateTimeOffset.UnixEpoch,
				LeaseExpiresAt = DateTimeOffset.MaxValue, ExecutionTraceId = "legacy-trace",
			}],
			Batches = [], Edges = [], RecurringSchedules = [],
		}),
	];

	private static async ValueTask InterruptAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var now = clock.GetUtcNow();
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = JobName, MaxConcurrency = 1, SlidingWindowMax = 10, SlidingWindowPeriod = TimeSpan.FromHours(1), FixedWindowMax = 10, FixedWindowPeriod = TimeSpan.FromHours(1) }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		var handle = JobHandle.FromString("abandoned-startup");
		await storage.EnqueueAsync(new() { JobHandle = handle, JobName = JobName, Payload = "{}", Context = "context", GroupId = "group", State = JobState.Pending, CreatedAt = now, DueAt = now }, token);
		JobAcquisitionRequest Request(string worker) => new()
		{
			WorkerId = worker,
			BatchSize = 1,
			Lease = TimeSpan.FromHours(1),
			FairQueues = new() { GroupRoundRobin = true, ConcurrencyShareThreshold = 0.5, MinInflightForNoisy = 1 },
			Queues = [new() { QueueName = "default", Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [JobName] = 1 } }],
		};
		var first = (await storage.AcquireDueJobsAsync(Request("old-worker"), token)).Single();
		await storage.SetExecutionTelemetryAsync(handle, first.Attempt, "old-worker", "trace", "span", now, token);
		await storage.InitializeAsync(interruptActiveLeases: false, token);
		ConformanceAssert.Equal(JobState.Active, (await storage.QueryJobsAsync(new() { JobHandle = handle }, token)).Single().State, Name, "ordinary initialization preserves live ownership");
		await storage.PauseJobAsync(JobName, token);
		clock.Advance(TimeSpan.FromSeconds(1));
		await storage.InitializeAsync(interruptActiveLeases: true, token);
		var recovered = (await storage.QueryJobsAsync(new() { JobHandle = handle }, token)).Single();
		ConformanceAssert.Equal(JobState.Pending, recovered.State, Name, "an unexpired active job becomes pending");
		ConformanceAssert.Null(recovered.WorkerId, Name, "recovery clears ownership");
		ConformanceAssert.Null(recovered.LeaseExpiresAt, Name, "recovery clears the lease");
		ConformanceAssert.Equal(first.Attempt, recovered.Attempt, Name, "only acquisition increments attempts");
		ConformanceAssert.Equal(first.DueAt, recovered.DueAt, Name, "recovery preserves queue ordering");
		ConformanceAssert.Equal(first.Payload, recovered.Payload, Name, "recovery preserves payload");
		ConformanceAssert.Equal(first.Context, recovered.Context, Name, "recovery preserves context");
		var execution = (await storage.QueryJobExecutionsAsync(handle, new(), token)).Single();
		ConformanceAssert.Equal(JobExecutionState.Interrupted, execution.State, Name, "history records interruption rather than failure");
		ConformanceAssert.Equal<DateTimeOffset?>(clock.GetUtcNow(), execution.CompletedAt, Name, "history ends at recovery time");
		ConformanceAssert.Equal("trace", execution.ExecutionTraceId, Name, "history retains telemetry");
		var status = await storage.GetJobAcquisitionStateAsync(JobName, token);
		ConformanceAssert.True(status.IsPaused, Name, "startup preserves definition pauses");
		ConformanceAssert.Equal(0, status.ActiveCount, Name, "recovery releases concurrency capacity");
		ConformanceAssert.Equal(1, status.SlidingWindowCount, Name, "startup preserves sliding-window consumption");
		ConformanceAssert.Equal(1, status.FixedWindowCount, Name, "startup preserves fixed-window consumption");
		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(() => storage.RenewLeaseAsync(handle, first.Attempt, "old-worker", TimeSpan.FromHours(1), token), Name, "the old owner is fenced immediately");
		clock.Advance(TimeSpan.FromSeconds(1));
		await storage.InitializeAsync(interruptActiveLeases: true, token);
		ConformanceAssert.Equal(execution, (await storage.QueryJobExecutionsAsync(handle, new(), token)).Single(), Name, "repeated recovery preserves interruption history");
		await storage.ResumeJobAsync(JobName, token);
		var next = (await storage.AcquireDueJobsAsync(Request("new-worker"), token)).Single();
		ConformanceAssert.Equal(first.Attempt + 1, next.Attempt, Name, "recovered work is immediately reacquirable with fair queues");
	}

	private static async ValueTask InterruptLegacyAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.InitializeAsync(interruptActiveLeases: true, token);
		var history = (await storage.QueryJobExecutionsAsync(JobHandle.FromString("legacy-startup"), new(), token)).Single();
		ConformanceAssert.True(history.IsSynthetic, LegacyName, "legacy history is materialized before ownership is cleared");
		ConformanceAssert.Equal(JobExecutionState.Interrupted, history.State, LegacyName, "legacy executions are interrupted");
		ConformanceAssert.Equal("old-worker", history.WorkerId, LegacyName, "legacy history preserves the prior owner");
		ConformanceAssert.Equal("legacy-trace", history.ExecutionTraceId, LegacyName, "legacy history preserves telemetry");
	}
}

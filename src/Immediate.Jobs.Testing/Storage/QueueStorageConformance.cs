using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class QueueStorageConformance
{
	private const string InitializeName = "Queue.Lifecycle.InitializesIdempotently";
	private const string HealthName = "Queue.Health.ReportsProvisionedBackendReachable";
	private const string RoundTripName = "Queue.Enqueue.RoundTripsRecordAndRejectsDuplicate";
	private const string DueName = "Queue.Acquisition.ExcludesFutureJobs";
	private const string CapacityName = "Queue.Acquisition.HonorsOrderingAndCapacities";
	private const string ContentionName = "Queue.Acquisition.ClaimsEachJobOnceUnderContention";
	private const string LeaseName = "Queue.Leases.ReclaimsExpiredLeaseAndRejectsStaleOwner";
	private const string CompletionName = "Queue.Executions.PersistsTelemetryCompletionAndHistory";
	private const string FailureName = "Queue.Executions.PersistsFailureRetryAndCancellation";
	private const string MutationName = "Queue.Mutations.RejectsMissingAndInvalidTransitions";
	private const string QueryName = "Queue.Queries.ComposesFiltersAndPagesDeterministically";
	private const string MonitoringName = "Queue.Monitoring.ReportsCountsStatusAndHeartbeatLiveness";
	private const string MaintenanceName = "Queue.Maintenance.DeletesAndPurgesTerminalHistory";
	private const string CancellationName = "Queue.Cancellation.ObservesPreCancelledOperation";
	private const string DisposeName = "Queue.Lifecycle.ToleratesRepeatedConcurrentDisposal";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(InitializeName, StorageCapabilities.Queue, InitializeAsync),
		new(HealthName, StorageCapabilities.Queue, IsHealthyAsync),
		new(RoundTripName, StorageCapabilities.Queue, RoundTripsRecordAsync),
		new(DueName, StorageCapabilities.Queue, ExcludesFutureAndParkedAsync),
		new(CapacityName, StorageCapabilities.Queue, HonorsOrderingAndCapacitiesAsync),
		new(ContentionName, StorageCapabilities.Queue, ClaimsOnceUnderContentionAsync),
		new(LeaseName, StorageCapabilities.Queue, ReclaimsExpiredLeaseAsync),
		new(CompletionName, StorageCapabilities.Queue, PersistsCompletionAsync),
		new(FailureName, StorageCapabilities.Queue, PersistsFailureRetryAndCancellationAsync),
		new(MutationName, StorageCapabilities.Queue, RejectsInvalidMutationsAsync),
		new(QueryName, StorageCapabilities.Queue, QueriesAndPagesAsync),
		new(MonitoringName, StorageCapabilities.Queue, ReportsMonitoringAsync),
		new(MaintenanceName, StorageCapabilities.Queue, DeletesAndPurgesAsync),
		new(CancellationName, StorageCapabilities.Queue, ObservesCancellationAsync),
		new(DisposeName, StorageCapabilities.Queue, DisposesIdempotentlyAsync),
	];

	private static async ValueTask InitializeAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		await storage.InitializeAsync(cancellationToken);
		await storage.InitializeAsync(cancellationToken);
	}

	private static async ValueTask IsHealthyAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var isHealthy = await storage.IsHealthyAsync(cancellationToken);

		ConformanceAssert.True(
			isHealthy,
			HealthName,
			"IsHealthyAsync must report a reachable, provisioned backend"
		);
	}

	private static async ValueTask RoundTripsRecordAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var original = CreateJob(
			"roundtrip-id:opaque/1",
			timeProvider.GetUtcNow(),
			"queue-roundtrip",
			"RoundTrip.Job"
		) with
		{
			Payload = "{\"message\":\"unchanged\",\"number\":42}",
			Context = "{\"tenant\":\"acme\"}",
			GroupId = "group-alpha",
			State = JobState.Scheduled,
			DueAt = timeProvider.GetUtcNow().AddMinutes(5),
			Attempt = 3,
			LastError = "prior error",
			RecurringKey = "schedule:638000000000000000",
			TraceParent = "00-11111111111111111111111111111111-2222222222222222-01",
			TraceState = "vendor=value",
		};

		await storage.EnqueueAsync(original, cancellationToken);

		var queried = ConformanceAssert.NotNull(
			(await storage.QueryJobsAsync(new() { JobHandle = original.JobHandle }, cancellationToken)).SingleOrDefault(),
			RoundTripName,
			"an enqueued record must be immediately visible by exact id",
			original.JobHandle.Value
		);

		AssertRecordEqual(original, queried, RoundTripName);

		await ConformanceAssert.ThrowsAnyAsync(
			() => storage.EnqueueAsync(original with { Payload = "{\"replacement\":true}" }, cancellationToken),
			RoundTripName,
			"a duplicate invocation identifier must fail",
			original.JobHandle.Value
		);

		var unchanged = (await storage.QueryJobsAsync(new() { JobHandle = original.JobHandle }, cancellationToken)).Single();
		AssertRecordEqual(original, unchanged, RoundTripName);
	}

	private static async ValueTask ExcludesFutureAndParkedAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();

		await storage.EnqueueAsync(CreateJob("due", now), cancellationToken);

		await storage.EnqueueAsync(
			CreateJob("future", now) with
			{
				State = JobState.Scheduled,
				DueAt = now.AddHours(1),
			},
			cancellationToken
		);

		var acquired = await storage.AcquireDueJobsAsync(
			CreateRequest("due-worker", 10),
			cancellationToken
		);

		ConformanceAssert.SequenceEqual(
			["due"],
			acquired.Select(static job => job.JobHandle.Value),
			DueName,
			"only due pending or scheduled records may be acquired",
			comparer: StringComparer.Ordinal
		);
	}

	private static async ValueTask HonorsOrderingAndCapacitiesAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();

		await storage.EnqueueAsync(CreateJob("low-first", now, "low", "low-job"), cancellationToken);
		await storage.EnqueueAsync(CreateJob("high-a-first", now.AddMilliseconds(1), "high", "limited-job") with { DueAt = now }, cancellationToken);
		await storage.EnqueueAsync(CreateJob("high-a-second", now.AddMilliseconds(2), "high", "limited-job") with { DueAt = now }, cancellationToken);
		await storage.EnqueueAsync(CreateJob("high-b", now.AddMilliseconds(3), "high", "other-job") with { DueAt = now }, cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(
			CreateRequest(
				"capacity-worker",
				3,
				("high", 2, CreateCapacities(("limited-job", 1), ("other-job", 1))),
				("low", 1, CreateCapacities(("low-job", 1)))
			),
			cancellationToken
		);

		ConformanceAssert.SequenceEqual(
			["high-a-first", "high-b", "low-first"],
			acquired.Select(static job => job.JobHandle.Value),
			CapacityName,
			"queue order, queue capacity, job-name capacity, and due order must compose",
			comparer: StringComparer.Ordinal
		);

		ConformanceAssert.True(
			acquired.All(job => job.State == JobState.Active && string.Equals(job.WorkerId, "capacity-worker", StringComparison.Ordinal) && job.Attempt == 1 && job.LeaseExpiresAt == now.AddMinutes(1)),
			CapacityName,
			"every acquired projection must contain active ownership, lease expiry, and execution ordinal"
		);
	}

	private static async ValueTask ClaimsOnceUnderContentionAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		const int JobCount = 20;

		var now = timeProvider.GetUtcNow();

		for (var index = 0; index < JobCount; index++)
			await storage.EnqueueAsync(CreateJob("contention-" + index.ToString("D2", CultureInfo.InvariantCulture), now.AddMilliseconds(index)) with { DueAt = now }, cancellationToken);

		var acquisitions = Enumerable.Range(0, 8)
			.Select(index => storage
				.AcquireDueJobsAsync(
					CreateRequest("contender-" + index.ToString(CultureInfo.InvariantCulture), JobCount),
					cancellationToken
				)
				.AsTask()
			)
			.ToList();

		var acquired = (await Task.WhenAll(acquisitions)).SelectMany(static jobs => jobs).ToList();

		ConformanceAssert.Equal(JobCount, acquired.Count, ContentionName, "concurrent acquisitions must collectively claim every eligible job");

		ConformanceAssert.Equal(
			JobCount,
			acquired.Select(static job => job.JobHandle).Distinct().Count(),
			ContentionName,
			"concurrent acquisitions must claim each invocation at most once"
		);
	}

	private static async ValueTask ReclaimsExpiredLeaseAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		await storage.EnqueueAsync(CreateJob("leased", timeProvider.GetUtcNow()), cancellationToken);

		var first = (await storage.AcquireDueJobsAsync(CreateRequest("owner-a", 1), cancellationToken)).Single();

		await storage.SetExecutionTelemetryAsync(first.JobHandle, first.Attempt, "owner-a", "trace-a", "span-a", timeProvider.GetUtcNow(), cancellationToken);

		timeProvider.Advance(TimeSpan.FromSeconds(30));

		await storage.RenewLeaseAsync(first.JobHandle, first.Attempt, "owner-a", TimeSpan.FromMinutes(2), cancellationToken);

		timeProvider.Advance(TimeSpan.FromMinutes(1));

		ConformanceAssert.Equal(
			0,
			(await storage.AcquireDueJobsAsync(
				CreateRequest("owner-b", 1),
				cancellationToken)
			).Count,
			LeaseName,
			"renewal by the current owner must extend the lease from storage time"
		);

		timeProvider.Advance(TimeSpan.FromMinutes(2));

		var second = (await storage.AcquireDueJobsAsync(CreateRequest("owner-b", 1), cancellationToken)).Single();

		ConformanceAssert.Equal(2, second.Attempt, LeaseName, "reclaiming an expired lease must begin a new execution ordinal", first.JobHandle.Value);
		ConformanceAssert.Null(second.ExecutionTraceId, LeaseName, "reclaim must clear latest-attempt trace telemetry", first.JobHandle.Value);
		ConformanceAssert.Null(second.ExecutionSpanId, LeaseName, "reclaim must clear latest-attempt span telemetry", first.JobHandle.Value);
		ConformanceAssert.Null(second.ExecutionStartedAt, LeaseName, "reclaim must clear latest-attempt start telemetry", first.JobHandle.Value);

		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.SetExecutionTelemetryAsync(first.JobHandle, first.Attempt, "owner-a", "first", "first", first.DueAt, cancellationToken),
			LeaseName,
			"a first execution owner must be fenced from every execution mutation",
			first.JobHandle.Value
		);

		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.RenewLeaseAsync(first.JobHandle, first.Attempt, "owner-a", TimeSpan.FromMinutes(1), cancellationToken),
			LeaseName,
			"a first execution owner must be fenced from every execution mutation",
			first.JobHandle.Value
		);

		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.CompleteAsync(first.JobHandle, first.Attempt, "owner-a", cancellationToken),
			LeaseName,
			"a first execution owner must be fenced from every execution mutation",
			first.JobHandle.Value
		);

		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.FailAsync(first.JobHandle, first.Attempt, "owner-a", "first", nextRetryAt: null, cancellationToken),
			LeaseName,
			"a first execution owner must be fenced from every execution mutation",
			first.JobHandle.Value
		);

		var history = await storage.QueryJobExecutionsAsync(first.JobHandle, new(), cancellationToken);

		ConformanceAssert.SequenceEqual(
			[2, 1],
			history.Select(static execution => execution.Attempt),
			LeaseName,
			"reclaim must retain the interrupted execution and create an active execution"
		);

		ConformanceAssert.Equal(
			JobExecutionState.Interrupted,
			history[1].State,
			LeaseName,
			"the expired execution must be retained as interrupted"
		);
	}

	private static async ValueTask PersistsCompletionAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();

		await storage.EnqueueAsync(CreateJob("completed", now), cancellationToken);

		var active = (await storage.AcquireDueJobsAsync(CreateRequest("completion-worker", 1), cancellationToken)).Single();

		var startedAt = now.AddSeconds(1);

		await storage.SetExecutionTelemetryAsync(active.JobHandle, active.Attempt, "completion-worker", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", startedAt, cancellationToken);

		timeProvider.Advance(TimeSpan.FromSeconds(2));

		await storage.CompleteAsync(active.JobHandle, active.Attempt, "completion-worker", cancellationToken);

		var job = (await storage.QueryJobsAsync(new() { JobHandle = active.JobHandle }, cancellationToken)).Single();
		var execution = (await storage.QueryJobExecutionsAsync(active.JobHandle, new(), cancellationToken)).Single();

		ConformanceAssert.Equal(JobState.Succeeded, job.State, CompletionName, "completion must atomically make the job successful");
		ConformanceAssert.Equal(JobExecutionState.Succeeded, execution.State, CompletionName, "completion must atomically retain a successful execution");
		ConformanceAssert.Equal(job.CompletedAt, execution.CompletedAt, CompletionName, "job and execution completion timestamps must agree");
		ConformanceAssert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", execution.ExecutionTraceId, CompletionName, "execution trace telemetry must be retained");
		ConformanceAssert.Equal("bbbbbbbbbbbbbbbb", execution.ExecutionSpanId, CompletionName, "execution span telemetry must be retained");
		ConformanceAssert.Equal(startedAt, execution.ExecutionStartedAt, CompletionName, "execution start telemetry must be retained");
	}

	private static async ValueTask PersistsFailureRetryAndCancellationAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		await storage.EnqueueAsync(CreateJob("retryable", timeProvider.GetUtcNow()), cancellationToken);

		var first = (await storage.AcquireDueJobsAsync(CreateRequest("failure-worker", 1), cancellationToken)).Single();
		var retryAt = timeProvider.GetUtcNow().AddMinutes(5);

		await storage.FailAsync(first.JobHandle, first.Attempt, "failure-worker", "retryable error", retryAt, cancellationToken);

		var scheduled = (await storage.QueryJobsAsync(new() { JobHandle = first.JobHandle }, cancellationToken)).Single();
		ConformanceAssert.Equal(JobState.Scheduled, scheduled.State, FailureName, "a future retry must schedule the job");
		ConformanceAssert.Equal(retryAt, scheduled.DueAt, FailureName, "a future retry must preserve its supplied due time");
		ConformanceAssert.Equal("retryable error", scheduled.LastError, FailureName, "a retryable failure must remain visible on the job projection");

		await storage.RetryAsync(first.JobHandle, cancellationToken);

		var fastForwarded = (await storage.QueryJobsAsync(new() { JobHandle = first.JobHandle }, cancellationToken)).Single();
		ConformanceAssert.Equal(JobState.Pending, fastForwarded.State, FailureName, "retrying a scheduled job must fast-forward it to pending");
		ConformanceAssert.Equal(first.Attempt, fastForwarded.Attempt, FailureName, "fast-forwarding must not create an execution attempt");
		ConformanceAssert.Equal("retryable error", fastForwarded.LastError, FailureName, "fast-forwarding a scheduled retry must preserve its latest error");

		var second = (await storage.AcquireDueJobsAsync(CreateRequest("failure-worker", 1), cancellationToken)).Single();
		await storage.FailAsync(second.JobHandle, second.Attempt, "failure-worker", "terminal error", nextRetryAt: null, cancellationToken);

		var failed = (await storage.QueryJobsAsync(new() { JobHandle = second.JobHandle }, cancellationToken)).Single();
		ConformanceAssert.Equal(JobState.Failed, failed.State, FailureName, "a failure without a retry time must be terminal");
		ConformanceAssert.Equal("terminal error", failed.LastError, FailureName, "terminal failure must retain its error");

		var executions = await storage.QueryJobExecutionsAsync(second.JobHandle, new(), cancellationToken);
		ConformanceAssert.SequenceEqual(
			[2, 1],
			executions.Select(static execution => execution.Attempt),
			FailureName,
			"execution history must be newest first"
		);

		ConformanceAssert.SequenceEqual(
			["terminal error", "retryable error"],
			executions.Select(static execution => execution.Error ?? "<null>"),
			FailureName,
			"each failed execution must retain its own error"
		);

		var newestPage = await storage.QueryJobExecutionsAsync(second.JobHandle, new() { Take = 1 }, cancellationToken);
		ConformanceAssert.Equal(2, newestPage.Single().Attempt, FailureName, "execution-history paging must return the newest attempt first");

		var olderPage = await storage.QueryJobExecutionsAsync(second.JobHandle, new() { Skip = 1, Take = 1 }, cancellationToken);
		ConformanceAssert.Equal(1, olderPage.Single().Attempt, FailureName, "execution-history skip/take paging must visit the older attempt");

		var exactAttempt = await storage.QueryJobExecutionsAsync(second.JobHandle, new() { Attempt = 1 }, cancellationToken);
		ConformanceAssert.Equal(1, exactAttempt.Single().Attempt, FailureName, "execution-history exact-attempt filtering must return only that attempt");

		await storage.RetryAsync(second.JobHandle, cancellationToken);
		var manuallyRetried = (await storage.QueryJobsAsync(new() { JobHandle = second.JobHandle }, cancellationToken)).Single();
		ConformanceAssert.Equal(JobState.Pending, manuallyRetried.State, FailureName, "retrying a terminal failure must return it to pending");
		ConformanceAssert.Equal(2, manuallyRetried.Attempt, FailureName, "manual retry must not create an execution attempt before acquisition");
		ConformanceAssert.Null(manuallyRetried.LastError, FailureName, "retrying a terminal failure must clear the projected error");
		ConformanceAssert.Null(manuallyRetried.CompletedAt, FailureName, "retrying a terminal failure must clear terminal completion time");

		var third = (await storage.AcquireDueJobsAsync(CreateRequest("failure-worker", 1), cancellationToken)).Single();
		await storage.CancelAsync(third.JobHandle, cancellationToken);

		var cancelled = (await storage.QueryJobExecutionsAsync(third.JobHandle, new() { Attempt = 3 }, cancellationToken)).Single();
		ConformanceAssert.Equal(JobExecutionState.Cancelled, cancelled.State, FailureName, "cancelling an active job must close its execution as cancelled");

		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.CompleteAsync(third.JobHandle, third.Attempt, "failure-worker", cancellationToken),
			FailureName,
			"cancellation must fence the former execution owner",
			third.JobHandle.Value
		);
	}

	private static async ValueTask RejectsInvalidMutationsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var jobHandle = JobHandle.FromString("pending-invalid");

		await storage.EnqueueAsync(CreateJob("pending-invalid", timeProvider.GetUtcNow()), cancellationToken);

		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.RetryAsync(jobHandle, cancellationToken),
			MutationName,
			"retry must reject a pending job without changing it"
		);

		await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.DeleteAsync(jobHandle, cancellationToken),
			MutationName,
			"delete must reject a non-terminal job without changing it"
		);

		var pending = (await storage.QueryJobsAsync(new() { JobHandle = jobHandle }, cancellationToken)).Single();
		ConformanceAssert.Equal(JobState.Pending, pending.State, MutationName, "invalid mutations must not partially change the target");

		await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => storage.CancelAsync(JobHandle.FromString("missing"), cancellationToken),
			MutationName,
			"dashboard mutation targets that do not exist must throw KeyNotFoundException"
		);

		await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => storage.RetryAsync(JobHandle.FromString("missing"), cancellationToken),
			MutationName,
			"dashboard mutation targets that do not exist must throw KeyNotFoundException"
		);

		await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => storage.DeleteAsync(JobHandle.FromString("missing"), cancellationToken),
			MutationName,
			"dashboard mutation targets that do not exist must throw KeyNotFoundException"
		);
	}

	private static async ValueTask QueriesAndPagesAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var created = timeProvider.GetUtcNow();

		await storage.EnqueueAsync(CreateJob("page-c", created, "priority", "Email.Receipt") with { State = JobState.Scheduled, DueAt = created.AddHours(1) }, cancellationToken);
		await storage.EnqueueAsync(CreateJob("page-a", created, "priority", "Email.Receipt") with { State = JobState.Scheduled, DueAt = created.AddHours(1) }, cancellationToken);
		await storage.EnqueueAsync(CreateJob("page-b", created, "priority", "Email.Other") with { State = JobState.Scheduled, DueAt = created.AddHours(1) }, cancellationToken);
		await storage.EnqueueAsync(CreateJob("newer", created.AddMinutes(1), "ordinary", "Cleanup.Job"), cancellationToken);

		var exact = await storage.QueryJobsAsync(new() { JobHandle = JobHandle.FromString("page-a") }, cancellationToken);

		ConformanceAssert.SequenceEqual(
			[JobHandle.FromString("page-a")],
			exact.Select(static job => job.JobHandle),
			QueryName,
			"exact-id lookup must be unaffected by unrelated records"
		);

		var filtered = await storage
			.QueryJobsAsync(new()
			{
				State = JobState.Scheduled,
				QueueName = "priority",
				JobName = "Email.Receipt",
				Search = "receipt",
			},
			cancellationToken
		);

		ConformanceAssert.SequenceEqual(
			["page-a", "page-c"],
			filtered.Select(static job => job.JobHandle.Value).Order(StringComparer.Ordinal),
			QueryName,
			"state, queue, exact-name, and case-insensitive search filters must compose"
		);

		var firstPage = await storage.QueryJobsAsync(new() { Skip = 0, Take = 2 }, cancellationToken);
		var secondPage = await storage.QueryJobsAsync(new() { Skip = 2, Take = 2 }, cancellationToken);
		var pagedIds = firstPage.Concat(secondPage).Select(static job => job.JobHandle.Value).ToList();

		ConformanceAssert.Equal(
			"newer",
			pagedIds[0],
			QueryName,
			"paging must order newer creation timestamps first"
		);

		ConformanceAssert.SequenceEqual(
			["newer", "page-a", "page-b", "page-c"],
			pagedIds.Order(StringComparer.Ordinal),
			QueryName,
			"paging tied creation timestamps must visit every record exactly once"
		);

		var repeatedIds = (await storage.QueryJobsAsync(new() { Skip = 0, Take = 1 }, cancellationToken))
			.Concat(await storage.QueryJobsAsync(new() { Skip = 1, Take = 3 }, cancellationToken))
			.Select(static job => job.JobHandle.Value);

		ConformanceAssert.SequenceEqual(
			pagedIds,
			repeatedIds,
			QueryName,
			"the provider's identifier tie-break order must be deterministic"
		);

		ConformanceAssert.Null(
			await storage.GetJobStatusAsync(JobHandle.FromString("missing"), cancellationToken),
			QueryName,
			"status lookup for a missing job must return null"
		);

		var status = ConformanceAssert.NotNull(
			await storage.GetJobStatusAsync(JobHandle.FromString("page-a"), cancellationToken),
			QueryName,
			"status lookup must return an existing job"
		);

		ConformanceAssert.Equal(0, status.DependsOn.Count, QueryName, "ordinary queue jobs must report no incoming graph edges");
	}

	private static async ValueTask ReportsMonitoringAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var now = timeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob("monitor-pending", now), cancellationToken);
		await storage.EnqueueAsync(CreateJob("monitor-scheduled", now) with { State = JobState.Scheduled, DueAt = now.AddHours(1) }, cancellationToken);
		await storage.EnqueueAsync(CreateJob("monitor-cancelled", now), cancellationToken);

		await storage.CancelAsync(JobHandle.FromString("monitor-cancelled"), cancellationToken);

		await storage.HeartbeatAsync(new() { WorkerId = "old-server", LastHeartbeat = now, ActiveWorkers = 1, MaxWorkers = 4 }, cancellationToken);

		timeProvider.Advance(TimeSpan.FromMinutes(3));

		var liveAt = timeProvider.GetUtcNow();

		await storage.HeartbeatAsync(new() { WorkerId = "live-server", LastHeartbeat = liveAt, ActiveWorkers = 2, MaxWorkers = 8 }, cancellationToken);

		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);

		ConformanceAssert.Equal(liveAt, snapshot.CapturedAt, MonitoringName, "monitoring capture time must use the registered storage clock");
		ConformanceAssert.Equal(1L, snapshot.Counts[JobState.Pending], MonitoringName, "monitoring pending count must agree with durable state");
		ConformanceAssert.Equal(1L, snapshot.Counts[JobState.Scheduled], MonitoringName, "monitoring scheduled count must agree with durable state");
		ConformanceAssert.Equal(1L, snapshot.Counts[JobState.Cancelled], MonitoringName, "monitoring cancelled count must agree with durable state");

		ConformanceAssert.SequenceEqual(
			["live-server"],
			snapshot.Servers.Select(static server => server.WorkerId),
			MonitoringName,
			"heartbeats must appear while live and disappear after the two-minute liveness window"
		);

		ConformanceAssert.Equal(storage.GetCapabilities(), snapshot.Capabilities, MonitoringName, "monitoring must report the resolved storage capabilities");
	}

	private static async ValueTask DeletesAndPurgesAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		await EnqueueAcquireAndComplete(storage, "delete-me", "maintenance-worker", timeProvider.GetUtcNow(), cancellationToken);

		await storage.DeleteAsync(JobHandle.FromString("delete-me"), cancellationToken);

		ConformanceAssert.Null(
			await storage.GetJobStatusAsync(JobHandle.FromString("delete-me"), cancellationToken),
			MaintenanceName,
			"deleting a terminal job must remove the job"
		);
		ConformanceAssert.Equal(
			0,
			(await storage.QueryJobExecutionsAsync(JobHandle.FromString("delete-me"), new(), cancellationToken)).Count,
			MaintenanceName,
			"deleting a terminal job must remove its execution history"
		);

		await EnqueueAcquireAndComplete(storage, "old-success", "maintenance-worker", timeProvider.GetUtcNow(), cancellationToken);

		await storage.EnqueueAsync(CreateJob("old-failure", timeProvider.GetUtcNow()), cancellationToken);

		var failure = (await storage.AcquireDueJobsAsync(CreateRequest("maintenance-worker", 1), cancellationToken)).Single();
		await storage.FailAsync(failure.JobHandle, failure.Attempt, "maintenance-worker", "old error", nextRetryAt: null, cancellationToken);

		await storage.EnqueueAsync(CreateJob("old-cancelled", timeProvider.GetUtcNow()), cancellationToken);
		await storage.CancelAsync(JobHandle.FromString("old-cancelled"), cancellationToken);

		timeProvider.Advance(TimeSpan.FromHours(2));

		await EnqueueAcquireAndComplete(storage, "recent-success", "maintenance-worker", timeProvider.GetUtcNow(), cancellationToken);

		await storage.PurgeJobsAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(3), cancellationToken);

		ConformanceAssert.Null(await storage.GetJobStatusAsync(JobHandle.FromString("old-success"), cancellationToken), MaintenanceName, "succeeded retention must remove an older successful job");
		ConformanceAssert.NotNull(await storage.GetJobStatusAsync(JobHandle.FromString("old-failure"), cancellationToken), MaintenanceName, "failed retention must be applied independently from succeeded retention");
		ConformanceAssert.NotNull(await storage.GetJobStatusAsync(JobHandle.FromString("old-cancelled"), cancellationToken), MaintenanceName, "cancelled jobs must use failed retention rather than succeeded retention");
		ConformanceAssert.NotNull(await storage.GetJobStatusAsync(JobHandle.FromString("recent-success"), cancellationToken), MaintenanceName, "purge must retain recent terminal jobs");
		ConformanceAssert.Equal(0, (await storage.QueryJobExecutionsAsync(JobHandle.FromString("old-success"), new(), cancellationToken)).Count, MaintenanceName, "purge must remove execution history with its job");

		timeProvider.Advance(TimeSpan.FromHours(2));
		await storage.PurgeJobsAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(3), cancellationToken);

		foreach (var id in new[] { JobHandle.FromString("old-failure"), JobHandle.FromString("old-cancelled") })
			ConformanceAssert.Null(await storage.GetJobStatusAsync(id, cancellationToken), MaintenanceName, "failed and cancelled retention must remove older jobs", id.Value);

		ConformanceAssert.Equal(
			0,
			(await storage.QueryJobExecutionsAsync(JobHandle.FromString("old-failure"), new(), cancellationToken)).Count,
			MaintenanceName,
			"failed-job purge must remove execution history with its job"
		);
	}

	private static async ValueTask ObservesCancellationAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		// ensure callee isn't throwing on the context cancellation token
		cancellationToken.ThrowIfCancellationRequested();

		using var cancellationSource = new CancellationTokenSource();
		await cancellationSource.CancelAsync();

		await ConformanceAssert.ThrowsAsync<OperationCanceledException>(
			async () => await storage.IsHealthyAsync(cancellationSource.Token),
			CancellationName,
			"a representative storage operation must observe a token cancelled before invocation"
		);
	}

	private static async ValueTask DisposesIdempotentlyAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await Task.WhenAll(storage.DisposeAsync().AsTask(), storage.DisposeAsync().AsTask());
		await storage.DisposeAsync();
	}

	private static async ValueTask EnqueueAcquireAndComplete(
		IJobStorage storage,
		string id,
		string workerId,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		await storage.EnqueueAsync(CreateJob(id, now), cancellationToken);
		var active = (await storage.AcquireDueJobsAsync(CreateRequest(workerId, 1), cancellationToken)).Single();
		await storage.CompleteAsync(active.JobHandle, active.Attempt, workerId, cancellationToken);
	}

	private static JobRecord CreateJob(
		string id,
		DateTimeOffset createdAt,
		string queueName = "default",
		string jobName = "conformance-job"
	) =>
		new()
		{
			JobHandle = JobHandle.FromString(id),
			QueueName = queueName,
			JobName = jobName,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = createdAt,
			CreatedAt = createdAt,
		};

	private static JobAcquisitionRequest CreateRequest(
		string workerId,
		int batchSize,
		params (string Queue, int Capacity, IReadOnlyDictionary<string, int> Jobs)[] queues
	) =>
		new()
		{
			WorkerId = workerId,
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = batchSize,
			Queues = queues.Length == 0
				? [new() { QueueName = "default", Capacity = batchSize, JobCapacities = CreateCapacities(("conformance-job", batchSize)) }]
				: [.. queues.Select(static queue => new JobQueueAcquisition { QueueName = queue.Queue, Capacity = queue.Capacity, JobCapacities = queue.Jobs })],
		};

	private static Dictionary<string, int> CreateCapacities(
		params (string JobName, int Capacity)[] capacities
	) => capacities.ToDictionary(static item => item.JobName, static item => item.Capacity, StringComparer.Ordinal);

	private static void AssertRecordEqual(JobRecord expected, JobRecord actual, string caseName)
	{
		var invariant = "all provider-neutral JobRecord fields must round-trip without rewriting";
		var context = expected.JobHandle.Value;

		ConformanceAssert.Equal(expected.QueueName, actual.QueueName, caseName, invariant, context);
		ConformanceAssert.Equal(expected.JobHandle, actual.JobHandle, caseName, invariant, context);
		ConformanceAssert.Equal(expected.JobName, actual.JobName, caseName, invariant, context);
		ConformanceAssert.Equal(expected.Payload, actual.Payload, caseName, invariant, context);
		ConformanceAssert.Equal(expected.Context, actual.Context, caseName, invariant, context);
		ConformanceAssert.Equal(expected.GroupId, actual.GroupId, caseName, invariant, context);
		ConformanceAssert.Equal(expected.State, actual.State, caseName, invariant, context);
		ConformanceAssert.Equal(expected.DueAt, actual.DueAt, caseName, invariant, context);
		ConformanceAssert.Equal(expected.CreatedAt, actual.CreatedAt, caseName, invariant, context);
		ConformanceAssert.Equal(expected.Attempt, actual.Attempt, caseName, invariant, context);
		ConformanceAssert.Equal(expected.WorkerId, actual.WorkerId, caseName, invariant, context);
		ConformanceAssert.Equal(expected.LeaseExpiresAt, actual.LeaseExpiresAt, caseName, invariant, context);
		ConformanceAssert.Equal(expected.LastError, actual.LastError, caseName, invariant, context);
		ConformanceAssert.Equal(expected.CompletedAt, actual.CompletedAt, caseName, invariant, context);
		ConformanceAssert.Equal(expected.RecurringKey, actual.RecurringKey, caseName, invariant, context);
		ConformanceAssert.Equal(expected.TraceParent, actual.TraceParent, caseName, invariant, context);
		ConformanceAssert.Equal(expected.TraceState, actual.TraceState, caseName, invariant, context);
		ConformanceAssert.Equal(expected.ExecutionTraceId, actual.ExecutionTraceId, caseName, invariant, context);
		ConformanceAssert.Equal(expected.ExecutionSpanId, actual.ExecutionSpanId, caseName, invariant, context);
		ConformanceAssert.Equal(expected.ExecutionStartedAt, actual.ExecutionStartedAt, caseName, invariant, context);
		ConformanceAssert.Equal(expected.BatchHandle, actual.BatchHandle, caseName, invariant, context);
		ConformanceAssert.Equal(expected.RemainingDependencies, actual.RemainingDependencies, caseName, invariant, context);
		ConformanceAssert.Equal(expected.FailedDependencies, actual.FailedDependencies, caseName, invariant, context);
	}
}

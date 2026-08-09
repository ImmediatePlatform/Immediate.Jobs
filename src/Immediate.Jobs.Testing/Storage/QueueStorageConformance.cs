using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

#pragma warning disable IDE0130
namespace Immediate.Jobs.Testing;

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

	internal static IReadOnlyList<JobStorageConformanceCaseDefinition> Cases { get; } =
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
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = serviceProvider;
		await storage.InitializeAsync(cancellationToken).ConfigureAwait(false);
		await storage.InitializeAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async ValueTask IsHealthyAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = serviceProvider;
		var isHealthy = await storage.IsHealthyAsync(cancellationToken).ConfigureAwait(false);
		ConformanceAssert.True(
			isHealthy,
			HealthName,
			"IsHealthyAsync must report a reachable, provisioned backend"
		);
	}

	private static async ValueTask RoundTripsRecordAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, RoundTripName);
		var now = clock.GetUtcNow();
		var original = CreateJob("roundtrip-id:opaque/1", now, "queue-roundtrip", "RoundTrip.Job") with
		{
			Payload = "{\"message\":\"unchanged\",\"number\":42}",
			Context = "{\"tenant\":\"acme\"}",
			GroupId = "group-alpha",
			State = JobState.Scheduled,
			DueAt = now.AddMinutes(5),
			Attempt = 3,
			LastError = "prior error",
			RecurringKey = "schedule:638000000000000000",
			TraceParent = "00-11111111111111111111111111111111-2222222222222222-01",
			TraceState = "vendor=value",
		};

		await storage.EnqueueAsync(original, cancellationToken).ConfigureAwait(false);
		var queried = ConformanceAssert.NotNull(
			(await storage.QueryJobsAsync(new() { Id = original.Id }, cancellationToken).ConfigureAwait(false)).SingleOrDefault(),
			RoundTripName,
			"an enqueued record must be immediately visible by exact id",
			original.Id
		);
		AssertRecordEqual(original, queried, RoundTripName);

		_ = await ConformanceAssert.ThrowsAnyAsync(
			() => storage.EnqueueAsync(original with { Payload = "{\"replacement\":true}" }, cancellationToken),
			RoundTripName,
			"a duplicate invocation identifier must fail",
			original.Id
		).ConfigureAwait(false);
		var unchanged = (await storage.QueryJobsAsync(new() { Id = original.Id }, cancellationToken).ConfigureAwait(false)).Single();
		AssertRecordEqual(original, unchanged, RoundTripName);
	}

	private static async ValueTask ExcludesFutureAndParkedAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, DueName);
		var now = clock.GetUtcNow();
		await storage.EnqueueAsync(CreateJob("due", now), cancellationToken).ConfigureAwait(false);
		await storage.EnqueueAsync(CreateJob("future", now) with
		{
			State = JobState.Scheduled,
			DueAt = now.AddHours(1),
		}, cancellationToken).ConfigureAwait(false);
		var acquired = await storage.AcquireDueJobsAsync(
			CreateRequest("due-worker", 10, ("default", 10, CreateCapacities(("conformance-job", 10)))),
			cancellationToken
		).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual(
			["due"],
			acquired.Select(static job => job.Id),
			DueName,
			"only due pending or scheduled records may be acquired"
		);
	}

	private static async ValueTask HonorsOrderingAndCapacitiesAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, CapacityName);
		var now = clock.GetUtcNow();
		await storage.EnqueueAsync(CreateJob("low-first", now, "low", "low-job"), cancellationToken).ConfigureAwait(false);
		await storage.EnqueueAsync(CreateJob("high-a-first", now.AddMilliseconds(1), "high", "limited-job") with { DueAt = now }, cancellationToken).ConfigureAwait(false);
		await storage.EnqueueAsync(CreateJob("high-a-second", now.AddMilliseconds(2), "high", "limited-job") with { DueAt = now }, cancellationToken).ConfigureAwait(false);
		await storage.EnqueueAsync(CreateJob("high-b", now.AddMilliseconds(3), "high", "other-job") with { DueAt = now }, cancellationToken).ConfigureAwait(false);

		var acquired = await storage.AcquireDueJobsAsync(
			CreateRequest(
				"capacity-worker",
				3,
				("high", 2, CreateCapacities(("limited-job", 1), ("other-job", 1))),
				("low", 1, CreateCapacities(("low-job", 1)))
			),
			cancellationToken
		).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual(
			["high-a-first", "high-b", "low-first"],
			acquired.Select(static job => job.Id),
			CapacityName,
			"queue order, queue capacity, job-name capacity, and due order must compose"
		);
		ConformanceAssert.True(
			acquired.All(job => job.State == JobState.Active && string.Equals(job.WorkerId, "capacity-worker", StringComparison.Ordinal) && job.Attempt == 1 && job.LeaseExpiresAt == now.AddMinutes(1)),
			CapacityName,
			"every acquired projection must contain active ownership, lease expiry, and execution ordinal"
		);
	}

	private static async ValueTask ClaimsOnceUnderContentionAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, ContentionName);
		const int JobCount = 20;
		for (var index = 0; index < JobCount; index++)
			await storage.EnqueueAsync(CreateJob("contention-" + index.ToString("D2", CultureInfo.InvariantCulture), clock.GetUtcNow().AddMilliseconds(index)) with { DueAt = clock.GetUtcNow() }, cancellationToken).ConfigureAwait(false);

		var acquisitions = Enumerable.Range(0, 8)
			.Select(index => storage.AcquireDueJobsAsync(
				CreateRequest("contender-" + index.ToString(CultureInfo.InvariantCulture), JobCount, ("default", JobCount, CreateCapacities(("conformance-job", JobCount)))),
				cancellationToken
			).AsTask())
			.ToArray();
		var acquired = (await Task.WhenAll(acquisitions).ConfigureAwait(false)).SelectMany(static jobs => jobs).ToArray();
		ConformanceAssert.Equal(JobCount, acquired.Length, ContentionName, "concurrent acquisitions must collectively claim every eligible job");
		ConformanceAssert.Equal(JobCount, acquired.Select(static job => job.Id).Distinct(StringComparer.Ordinal).Count(), ContentionName, "concurrent acquisitions must claim each invocation at most once");
	}

	private static async ValueTask ReclaimsExpiredLeaseAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, LeaseName);
		await storage.EnqueueAsync(CreateJob("leased", clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
		var first = (await storage.AcquireDueJobsAsync(CreateRequest("owner-a", 1), cancellationToken).ConfigureAwait(false)).Single();
		await storage.SetExecutionTelemetryAsync(first.Id, first.Attempt, "owner-a", "trace-a", "span-a", clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
		clock.Advance(TimeSpan.FromSeconds(30));
		await storage.RenewLeaseAsync(first.Id, first.Attempt, "owner-a", TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
		clock.Advance(TimeSpan.FromMinutes(1));
		ConformanceAssert.Equal(0, (await storage.AcquireDueJobsAsync(CreateRequest("owner-b", 1), cancellationToken).ConfigureAwait(false)).Count, LeaseName, "renewal by the current owner must extend the lease from storage time");
		clock.Advance(TimeSpan.FromMinutes(2));
		var second = (await storage.AcquireDueJobsAsync(CreateRequest("owner-b", 1), cancellationToken).ConfigureAwait(false)).Single();
		ConformanceAssert.Equal(2, second.Attempt, LeaseName, "reclaiming an expired lease must begin a new execution ordinal", first.Id);
		ConformanceAssert.Null(second.ExecutionTraceId, LeaseName, "reclaim must clear latest-attempt trace telemetry", first.Id);
		ConformanceAssert.Null(second.ExecutionSpanId, LeaseName, "reclaim must clear latest-attempt span telemetry", first.Id);
		ConformanceAssert.Null(second.ExecutionStartedAt, LeaseName, "reclaim must clear latest-attempt start telemetry", first.Id);

		await AssertStaleOwnerRejected(storage, first, cancellationToken).ConfigureAwait(false);
		var history = await storage.QueryJobExecutionsAsync(new() { JobId = first.Id }, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual([2, 1], history.Select(static execution => execution.Attempt), LeaseName, "reclaim must retain the interrupted execution and create an active execution");
		ConformanceAssert.Equal(JobExecutionState.Interrupted, history[1].State, LeaseName, "the expired execution must be retained as interrupted");
	}

	private static async ValueTask PersistsCompletionAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, CompletionName);
		await storage.EnqueueAsync(CreateJob("completed", clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
		var active = (await storage.AcquireDueJobsAsync(CreateRequest("completion-worker", 1), cancellationToken).ConfigureAwait(false)).Single();
		var startedAt = clock.GetUtcNow().AddSeconds(1);
		await storage.SetExecutionTelemetryAsync(active.Id, active.Attempt, "completion-worker", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", startedAt, cancellationToken).ConfigureAwait(false);
		clock.Advance(TimeSpan.FromSeconds(2));
		await storage.CompleteAsync(active.Id, active.Attempt, "completion-worker", cancellationToken).ConfigureAwait(false);

		var job = (await storage.QueryJobsAsync(new() { Id = active.Id }, cancellationToken).ConfigureAwait(false)).Single();
		var execution = (await storage.QueryJobExecutionsAsync(new() { JobId = active.Id }, cancellationToken).ConfigureAwait(false)).Single();
		ConformanceAssert.Equal(JobState.Succeeded, job.State, CompletionName, "completion must atomically make the job successful");
		ConformanceAssert.Equal(JobExecutionState.Succeeded, execution.State, CompletionName, "completion must atomically retain a successful execution");
		ConformanceAssert.Equal(job.CompletedAt, execution.CompletedAt, CompletionName, "job and execution completion timestamps must agree");
		ConformanceAssert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", execution.ExecutionTraceId, CompletionName, "execution trace telemetry must be retained");
		ConformanceAssert.Equal("bbbbbbbbbbbbbbbb", execution.ExecutionSpanId, CompletionName, "execution span telemetry must be retained");
		ConformanceAssert.Equal(startedAt, execution.ExecutionStartedAt, CompletionName, "execution start telemetry must be retained");
	}

	private static async ValueTask PersistsFailureRetryAndCancellationAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, FailureName);
		await storage.EnqueueAsync(CreateJob("retryable", clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
		var first = (await storage.AcquireDueJobsAsync(CreateRequest("failure-worker", 1), cancellationToken).ConfigureAwait(false)).Single();
		var retryAt = clock.GetUtcNow().AddMinutes(5);
		await storage.FailAsync(first.Id, first.Attempt, "failure-worker", "retryable error", retryAt, cancellationToken).ConfigureAwait(false);
		var scheduled = (await storage.QueryJobsAsync(new() { Id = first.Id }, cancellationToken).ConfigureAwait(false)).Single();
		ConformanceAssert.Equal(JobState.Scheduled, scheduled.State, FailureName, "a future retry must schedule the job");
		ConformanceAssert.Equal(retryAt, scheduled.DueAt, FailureName, "a future retry must preserve its supplied due time");
		ConformanceAssert.Equal("retryable error", scheduled.LastError, FailureName, "a retryable failure must remain visible on the job projection");

		await storage.RetryAsync(first.Id, cancellationToken).ConfigureAwait(false);
		var fastForwarded = (await storage.QueryJobsAsync(new() { Id = first.Id }, cancellationToken).ConfigureAwait(false)).Single();
		ConformanceAssert.Equal(JobState.Pending, fastForwarded.State, FailureName, "retrying a scheduled job must fast-forward it to pending");
		ConformanceAssert.Equal(first.Attempt, fastForwarded.Attempt, FailureName, "fast-forwarding must not create an execution attempt");
		ConformanceAssert.Equal("retryable error", fastForwarded.LastError, FailureName, "fast-forwarding a scheduled retry must preserve its latest error");

		var second = (await storage.AcquireDueJobsAsync(CreateRequest("failure-worker", 1), cancellationToken).ConfigureAwait(false)).Single();
		await storage.FailAsync(second.Id, second.Attempt, "failure-worker", "terminal error", nextRetryAt: null, cancellationToken).ConfigureAwait(false);
		var failed = (await storage.QueryJobsAsync(new() { Id = second.Id }, cancellationToken).ConfigureAwait(false)).Single();
		ConformanceAssert.Equal(JobState.Failed, failed.State, FailureName, "a failure without a retry time must be terminal");
		ConformanceAssert.Equal("terminal error", failed.LastError, FailureName, "terminal failure must retain its error");
		var executions = await storage.QueryJobExecutionsAsync(new() { JobId = second.Id }, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual([2, 1], executions.Select(static execution => execution.Attempt), FailureName, "execution history must be newest first");
		ConformanceAssert.SequenceEqual(["terminal error", "retryable error"], executions.Select(static execution => execution.Error ?? "<null>"), FailureName, "each failed execution must retain its own error");
		var newestPage = await storage.QueryJobExecutionsAsync(new() { JobId = second.Id, Take = 1 }, cancellationToken).ConfigureAwait(false);
		var olderPage = await storage.QueryJobExecutionsAsync(new() { JobId = second.Id, Skip = 1, Take = 1 }, cancellationToken).ConfigureAwait(false);
		var exactAttempt = await storage.QueryJobExecutionsAsync(new() { JobId = second.Id, Attempt = 1 }, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Equal(2, newestPage.Single().Attempt, FailureName, "execution-history paging must return the newest attempt first");
		ConformanceAssert.Equal(1, olderPage.Single().Attempt, FailureName, "execution-history skip/take paging must visit the older attempt");
		ConformanceAssert.Equal(1, exactAttempt.Single().Attempt, FailureName, "execution-history exact-attempt filtering must return only that attempt");

		await storage.RetryAsync(second.Id, cancellationToken).ConfigureAwait(false);
		var manuallyRetried = (await storage.QueryJobsAsync(new() { Id = second.Id }, cancellationToken).ConfigureAwait(false)).Single();
		ConformanceAssert.Equal(JobState.Pending, manuallyRetried.State, FailureName, "retrying a terminal failure must return it to pending");
		ConformanceAssert.Equal(2, manuallyRetried.Attempt, FailureName, "manual retry must not create an execution attempt before acquisition");
		ConformanceAssert.Null(manuallyRetried.LastError, FailureName, "retrying a terminal failure must clear the projected error");
		ConformanceAssert.Null(manuallyRetried.CompletedAt, FailureName, "retrying a terminal failure must clear terminal completion time");
		var third = (await storage.AcquireDueJobsAsync(CreateRequest("failure-worker", 1), cancellationToken).ConfigureAwait(false)).Single();
		await storage.CancelAsync(third.Id, cancellationToken).ConfigureAwait(false);
		var cancelled = (await storage.QueryJobExecutionsAsync(new() { JobId = third.Id, Attempt = 3 }, cancellationToken).ConfigureAwait(false)).Single();
		ConformanceAssert.Equal(JobExecutionState.Cancelled, cancelled.State, FailureName, "cancelling an active job must close its execution as cancelled");
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.CompleteAsync(third.Id, third.Attempt, "failure-worker", cancellationToken),
			FailureName,
			"cancellation must fence the former execution owner",
			third.Id
		).ConfigureAwait(false);
	}

	private static async ValueTask RejectsInvalidMutationsAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, MutationName);
		await storage.EnqueueAsync(CreateJob("pending-invalid", clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(() => storage.RetryAsync("pending-invalid", cancellationToken), MutationName, "retry must reject a pending job without changing it").ConfigureAwait(false);
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(() => storage.DeleteAsync("pending-invalid", cancellationToken), MutationName, "delete must reject a non-terminal job without changing it").ConfigureAwait(false);
		var pending = (await storage.QueryJobsAsync(new() { Id = "pending-invalid" }, cancellationToken).ConfigureAwait(false)).Single();
		ConformanceAssert.Equal(JobState.Pending, pending.State, MutationName, "invalid mutations must not partially change the target");

		foreach (var operation in new Func<ValueTask>[]
		{
			() => storage.CancelAsync("missing", cancellationToken),
			() => storage.RetryAsync("missing", cancellationToken),
			() => storage.DeleteAsync("missing", cancellationToken),
		})
			_ = await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(operation, MutationName, "dashboard mutation targets that do not exist must throw KeyNotFoundException").ConfigureAwait(false);
	}

	private static async ValueTask QueriesAndPagesAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, QueryName);
		var created = clock.GetUtcNow();
		var records = new[]
		{
			CreateJob("page-c", created, "priority", "Email.Receipt") with { State = JobState.Scheduled, DueAt = created.AddHours(1) },
			CreateJob("page-a", created, "priority", "Email.Receipt") with { State = JobState.Scheduled, DueAt = created.AddHours(1) },
			CreateJob("page-b", created, "priority", "Email.Other") with { State = JobState.Scheduled, DueAt = created.AddHours(1) },
			CreateJob("newer", created.AddMinutes(1), "ordinary", "Cleanup.Job"),
		};
		foreach (var record in records)
			await storage.EnqueueAsync(record, cancellationToken).ConfigureAwait(false);

		var exact = await storage.QueryJobsAsync(new() { Id = "page-a" }, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual(["page-a"], exact.Select(static job => job.Id), QueryName, "exact-id lookup must be unaffected by unrelated records");
		var filtered = await storage.QueryJobsAsync(new()
		{
			State = JobState.Scheduled,
			QueueName = "priority",
			JobName = "Email.Receipt",
			Search = "receipt",
		}, cancellationToken).ConfigureAwait(false);
		ConformanceAssert.SequenceEqual(
			["page-a", "page-c"],
			filtered.Select(static job => job.Id).Order(StringComparer.Ordinal),
			QueryName,
			"state, queue, exact-name, and case-insensitive search filters must compose"
		);

		var firstPage = await storage.QueryJobsAsync(new() { Skip = 0, Take = 2 }, cancellationToken).ConfigureAwait(false);
		var secondPage = await storage.QueryJobsAsync(new() { Skip = 2, Take = 2 }, cancellationToken).ConfigureAwait(false);
		var pagedIds = firstPage.Concat(secondPage).Select(static job => job.Id).ToArray();
		ConformanceAssert.Equal("newer", pagedIds[0], QueryName, "paging must order newer creation timestamps first");
		ConformanceAssert.SequenceEqual(
			["newer", "page-a", "page-b", "page-c"],
			pagedIds.Order(StringComparer.Ordinal),
			QueryName,
			"paging tied creation timestamps must visit every record exactly once"
		);
		var repeatedIds = (await storage.QueryJobsAsync(new() { Skip = 0, Take = 2 }, cancellationToken).ConfigureAwait(false))
			.Concat(await storage.QueryJobsAsync(new() { Skip = 2, Take = 2 }, cancellationToken).ConfigureAwait(false))
			.Select(static job => job.Id);
		ConformanceAssert.SequenceEqual(pagedIds, repeatedIds, QueryName,
			"the provider's identifier tie-break order must be deterministic");
		ConformanceAssert.Null(await storage.GetJobStatusAsync("missing", cancellationToken).ConfigureAwait(false), QueryName, "status lookup for a missing job must return null");
		var status = ConformanceAssert.NotNull(await storage.GetJobStatusAsync("page-a", cancellationToken).ConfigureAwait(false), QueryName, "status lookup must return an existing job");
		ConformanceAssert.Null(status.MaxAttempts, QueryName, "low-level storage status cannot infer a handler retry limit");
		ConformanceAssert.Equal(0, status.DependsOn.Count, QueryName, "ordinary queue jobs must report no incoming graph edges");
	}

	private static async ValueTask ReportsMonitoringAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, MonitoringName);
		var now = clock.GetUtcNow();
		await storage.EnqueueAsync(CreateJob("monitor-pending", now), cancellationToken).ConfigureAwait(false);
		await storage.EnqueueAsync(CreateJob("monitor-scheduled", now) with { State = JobState.Scheduled, DueAt = now.AddHours(1) }, cancellationToken).ConfigureAwait(false);
		await storage.EnqueueAsync(CreateJob("monitor-cancelled", now), cancellationToken).ConfigureAwait(false);
		await storage.CancelAsync("monitor-cancelled", cancellationToken).ConfigureAwait(false);
		await storage.HeartbeatAsync(new() { WorkerId = "old-server", LastHeartbeat = now, ActiveWorkers = 1, MaxWorkers = 4 }, cancellationToken).ConfigureAwait(false);
		clock.Advance(TimeSpan.FromMinutes(3));
		var liveAt = clock.GetUtcNow();
		await storage.HeartbeatAsync(new() { WorkerId = "live-server", LastHeartbeat = liveAt, ActiveWorkers = 2, MaxWorkers = 8 }, cancellationToken).ConfigureAwait(false);

		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Equal(liveAt, snapshot.CapturedAt, MonitoringName, "monitoring capture time must use the registered storage clock");
		ConformanceAssert.Equal(1L, snapshot.Counts[JobState.Pending], MonitoringName, "monitoring pending count must agree with durable state");
		ConformanceAssert.Equal(1L, snapshot.Counts[JobState.Scheduled], MonitoringName, "monitoring scheduled count must agree with durable state");
		ConformanceAssert.Equal(1L, snapshot.Counts[JobState.Cancelled], MonitoringName, "monitoring cancelled count must agree with durable state");
		ConformanceAssert.SequenceEqual(["live-server"], snapshot.Servers.Select(static server => server.WorkerId), MonitoringName, "heartbeats must appear while live and disappear after the two-minute liveness window");
		ConformanceAssert.Equal(storage.GetCapabilities(), snapshot.Capabilities, MonitoringName, "monitoring must report the resolved storage capabilities");
	}

	private static async ValueTask DeletesAndPurgesAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		var clock = GetClock(serviceProvider, MaintenanceName);
		await EnqueueAcquireAndComplete(storage, "delete-me", "maintenance-worker", clock, cancellationToken).ConfigureAwait(false);
		await storage.DeleteAsync("delete-me", cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Null(await storage.GetJobStatusAsync("delete-me", cancellationToken).ConfigureAwait(false), MaintenanceName, "deleting a terminal job must remove the job");
		ConformanceAssert.Equal(0, (await storage.QueryJobExecutionsAsync(new() { JobId = "delete-me" }, cancellationToken).ConfigureAwait(false)).Count, MaintenanceName, "deleting a terminal job must remove its execution history");

		await EnqueueAcquireAndComplete(storage, "old-success", "maintenance-worker", clock, cancellationToken).ConfigureAwait(false);
		await storage.EnqueueAsync(CreateJob("old-failure", clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
		var failure = (await storage.AcquireDueJobsAsync(CreateRequest("maintenance-worker", 1), cancellationToken).ConfigureAwait(false)).Single();
		await storage.FailAsync(failure.Id, failure.Attempt, "maintenance-worker", "old error", nextRetryAt: null, cancellationToken).ConfigureAwait(false);
		await storage.EnqueueAsync(CreateJob("old-cancelled", clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
		await storage.CancelAsync("old-cancelled", cancellationToken).ConfigureAwait(false);
		clock.Advance(TimeSpan.FromHours(2));
		await EnqueueAcquireAndComplete(storage, "recent-success", "maintenance-worker", clock, cancellationToken).ConfigureAwait(false);
		await storage.PurgeJobsAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(3), cancellationToken).ConfigureAwait(false);
		ConformanceAssert.Null(await storage.GetJobStatusAsync("old-success", cancellationToken).ConfigureAwait(false), MaintenanceName, "succeeded retention must remove an older successful job");
		_ = ConformanceAssert.NotNull(await storage.GetJobStatusAsync("old-failure", cancellationToken).ConfigureAwait(false), MaintenanceName, "failed retention must be applied independently from succeeded retention");
		_ = ConformanceAssert.NotNull(await storage.GetJobStatusAsync("old-cancelled", cancellationToken).ConfigureAwait(false), MaintenanceName, "cancelled jobs must use failed retention rather than succeeded retention");
		_ = ConformanceAssert.NotNull(await storage.GetJobStatusAsync("recent-success", cancellationToken).ConfigureAwait(false), MaintenanceName, "purge must retain recent terminal jobs");
		ConformanceAssert.Equal(0, (await storage.QueryJobExecutionsAsync(new() { JobId = "old-success" }, cancellationToken).ConfigureAwait(false)).Count, MaintenanceName, "purge must remove execution history with its job");

		clock.Advance(TimeSpan.FromHours(2));
		await storage.PurgeJobsAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(3), cancellationToken).ConfigureAwait(false);
		foreach (var id in new[] { "old-failure", "old-cancelled" })
			ConformanceAssert.Null(await storage.GetJobStatusAsync(id, cancellationToken).ConfigureAwait(false), MaintenanceName, "failed and cancelled retention must remove older jobs", id);
		ConformanceAssert.Equal(0, (await storage.QueryJobExecutionsAsync(new() { JobId = "old-failure" }, cancellationToken).ConfigureAwait(false)).Count, MaintenanceName, "failed-job purge must remove execution history with its job");
	}

	private static async ValueTask ObservesCancellationAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = serviceProvider;
		cancellationToken.ThrowIfCancellationRequested();

		using var cancellationSource = new CancellationTokenSource();
		await cancellationSource.CancelAsync().ConfigureAwait(false);
		_ = await ConformanceAssert.ThrowsAsync<OperationCanceledException>(
			() => ObserveHealthAsync(storage, cancellationSource.Token),
			CancellationName,
			"a representative storage operation must observe a token cancelled before invocation"
		).ConfigureAwait(false);
	}

	private static async ValueTask DisposesIdempotentlyAsync(
		IJobStorage storage,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken
	)
	{
		_ = serviceProvider;
		cancellationToken.ThrowIfCancellationRequested();
		await Task.WhenAll(storage.DisposeAsync().AsTask(), storage.DisposeAsync().AsTask()).ConfigureAwait(false);
		await storage.DisposeAsync().ConfigureAwait(false);
	}

	private static async ValueTask AssertStaleOwnerRejected(
		IJobStorage storage,
		JobRecord stale,
		CancellationToken cancellationToken
	)
	{
		var operations = new Func<ValueTask>[]
		{
			() => storage.SetExecutionTelemetryAsync(stale.Id, stale.Attempt, "owner-a", "stale", "stale", stale.DueAt, cancellationToken),
			() => storage.RenewLeaseAsync(stale.Id, stale.Attempt, "owner-a", TimeSpan.FromMinutes(1), cancellationToken),
			() => storage.CompleteAsync(stale.Id, stale.Attempt, "owner-a", cancellationToken),
			() => storage.FailAsync(stale.Id, stale.Attempt, "owner-a", "stale", nextRetryAt: null, cancellationToken),
		};
		foreach (var operation in operations)
			_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(operation, LeaseName, "a stale execution owner must be fenced from every execution mutation", stale.Id).ConfigureAwait(false);
	}

	private static async ValueTask EnqueueAcquireAndComplete(
		IJobStorage storage,
		string id,
		string workerId,
		FakeTimeProvider clock,
		CancellationToken cancellationToken
	)
	{
		await storage.EnqueueAsync(CreateJob(id, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
		var active = (await storage.AcquireDueJobsAsync(CreateRequest(workerId, 1), cancellationToken).ConfigureAwait(false)).Single();
		await storage.CompleteAsync(active.Id, active.Attempt, workerId, cancellationToken).ConfigureAwait(false);
	}

	private static FakeTimeProvider GetClock(IServiceProvider serviceProvider, string caseName)
	{
		var timeProvider = serviceProvider.GetService<TimeProvider>();
		return ConformanceAssert.IsAssignableFrom<FakeTimeProvider>(
			timeProvider,
			caseName,
			"time-dependent conformance cases require a FakeTimeProvider registered as TimeProvider"
		);
	}

	private static JobRecord CreateJob(
		string id,
		DateTimeOffset createdAt,
		string queueName = "default",
		string jobName = "conformance-job"
	) => new()
	{
		Id = id,
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
	) => new()
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
		var context = expected.Id;
		ConformanceAssert.Equal(expected.QueueName, actual.QueueName, caseName, invariant, context);
		ConformanceAssert.Equal(expected.Id, actual.Id, caseName, invariant, context);
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
		ConformanceAssert.Equal(expected.BatchId, actual.BatchId, caseName, invariant, context);
		ConformanceAssert.Equal(expected.RemainingDependencies, actual.RemainingDependencies, caseName, invariant, context);
		ConformanceAssert.Equal(expected.FailedDependencies, actual.FailedDependencies, caseName, invariant, context);
	}

	private static async ValueTask ObserveHealthAsync(IJobStorage storage, CancellationToken cancellationToken) =>
		_ = await storage.IsHealthyAsync(cancellationToken).ConfigureAwait(false);
}

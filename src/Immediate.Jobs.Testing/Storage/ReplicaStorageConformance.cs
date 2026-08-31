using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;
using Microsoft.Extensions.Time.Testing;

#pragma warning disable IDE0130
namespace Immediate.Jobs.Testing;

internal static class ReplicaStorageConformance
{
	private const string CapabilityName = "Replica.Capability.ResolvesAdvertisedStorage";
	private const string ExactDueName = "Replica.Acquisition.ClaimsExactlyTheRequestedDueJobs";
	private const string DuplicateName = "Replica.Acquisition.IgnoresDuplicateAndMissingRequestedIds";
	private const string ProjectionName = "Replica.Acquisition.PersistsOwnershipLeaseAttemptAndHistory";
	private const string ContentionName = "Replica.Acquisition.ClaimsEachInvocationVersionOnceUnderContention";
	private const string StaleName = "Replica.Leases.ReclaimsExpiredExecutionAndFencesStaleOwner";
	private const string FieldsName = "Replica.Projection.PreservesRecurringAndGraphRecoveryFields";
	private const string QueueName = "conformance-replica";
	private const string JobName = "conformance-replica-job";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(CapabilityName, StorageCapabilities.Replica, ResolvesAdvertisedStorage),
		new(ExactDueName, StorageCapabilities.Replica, ClaimsExactlyRequestedDueJobsAsync),
		new(DuplicateName, StorageCapabilities.Replica, IgnoresDuplicateAndMissingIdsAsync),
		new(ProjectionName, StorageCapabilities.Replica, PersistsProjectionAndHistoryAsync),
		new(ContentionName, StorageCapabilities.Replica, ClaimsOnceUnderContentionAsync),
		new(StaleName, StorageCapabilities.Replica, ReclaimsAndFencesStaleOwnerAsync),
		new(FieldsName, StorageCapabilities.Replica, PreservesRecoveryFieldsAsync),
	];

	private static ValueTask ResolvesAdvertisedStorage(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_ = Replica(storage, CapabilityName);
		return ValueTask.CompletedTask;
	}

	private static async ValueTask ClaimsExactlyRequestedDueJobsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var replica = Replica(storage, ExactDueName);
		var now = timeProvider.GetUtcNow();
		var requestedPending = Job("exact-pending", now);
		var requestedScheduled = Job("exact-scheduled", now) with { State = JobState.Scheduled };
		var requestedFuture = Job("exact-future", now) with
		{
			State = JobState.Scheduled,
			DueAt = now.AddMinutes(1),
		};
		var requestedParked = Job("exact-parked", now) with { State = JobState.AwaitingParameters };
		var unrequested = Job("exact-unrequested", now);
		var unavailable = Job("exact-unavailable", now);
		foreach (var job in new[]
		{
			requestedPending,
			requestedScheduled,
			requestedFuture,
			requestedParked,
			unrequested,
			unavailable,
		})
		{
			await storage.EnqueueAsync(job, cancellationToken);
		}

		_ = await replica.AcquireJobsAsync(
			[unavailable.JobHandle],
			"exact-existing-owner",
			TimeSpan.FromMinutes(5),
			cancellationToken
		);

		var acquired = await replica.AcquireJobsAsync(
			[
				requestedPending.JobHandle,
				requestedScheduled.JobHandle,
				requestedFuture.JobHandle,
				requestedParked.JobHandle,
				unavailable.JobHandle,
				JobHandle.FromString("exact-missing"),
			],
			"exact-worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		);
		ConformanceAssert.SequenceEqual(
			new[] { requestedPending.JobHandle.Value, requestedScheduled.JobHandle.Value }.Order(StringComparer.Ordinal),
			acquired.Select(static job => job.JobHandle.Value).Order(StringComparer.Ordinal),
			ExactDueName,
			"exact acquisition must claim only requested, due, currently available jobs"
		);
		var untouched = await GetJobAsync(storage, unrequested.JobHandle, ExactDueName, cancellationToken);
		ConformanceAssert.Equal(JobState.Pending, untouched.State, ExactDueName, "an unrequested due job must remain available");
	}

	private static async ValueTask IgnoresDuplicateAndMissingIdsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var replica = Replica(storage, DuplicateName);
		var now = timeProvider.GetUtcNow();
		var job = Job("duplicate-request", now);
		await storage.EnqueueAsync(job, cancellationToken);

		var acquired = await replica.AcquireJobsAsync(
			[job.JobHandle, JobHandle.FromString("duplicate-missing"), job.JobHandle, JobHandle.FromString("duplicate-missing")],
			"duplicate-worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		);
		ConformanceAssert.SequenceEqual(
			[job.JobHandle],
			acquired.Select(static item => item.JobHandle),
			DuplicateName,
			"duplicate and missing requested identifiers must not duplicate acquisition results"
		);
		ConformanceAssert.Equal(
			0,
			(await replica.AcquireJobsAsync([], "duplicate-worker-2", TimeSpan.FromMinutes(1), cancellationToken)).Count,
			DuplicateName,
			"an empty exact-id request must return an empty result"
		);
	}

	private static async ValueTask PersistsProjectionAndHistoryAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var replica = Replica(storage, ProjectionName);
		var now = timeProvider.GetUtcNow();
		var lease = TimeSpan.FromMinutes(3);
		var job = Job("projection-history", now) with
		{
			Context = "{\"tenant\":\"replica-projection\"}",
			GroupId = "replica-group",
			TraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
			TraceState = "conformance=replica",
		};
		await storage.EnqueueAsync(job, cancellationToken);

		var active = ConformanceAssert.NotNull(
			(await replica.AcquireJobsAsync([job.JobHandle], "projection-worker", lease, cancellationToken))
				.SingleOrDefault(),
			ProjectionName,
			"a requested due job must be returned"
		);
		AssertAcquired(job, active, "projection-worker", now + lease, ProjectionName);
		var executions = await storage.QueryJobExecutionsAsync(
			job.JobHandle,
			new(),
			cancellationToken
		);
		var activeExecution = ConformanceAssert.NotNull(
			executions.SingleOrDefault(),
			ProjectionName,
			"exact acquisition must create one retained execution"
		);
		ConformanceAssert.Equal(1, activeExecution.Attempt, ProjectionName, "the first execution ordinal must be one");
		ConformanceAssert.Equal(JobExecutionState.Active, activeExecution.State, ProjectionName, "the retained execution must be active");
		ConformanceAssert.Equal("projection-worker", activeExecution.WorkerId, ProjectionName, "execution history must retain its owner");
		ConformanceAssert.Equal(now, activeExecution.AcquiredAt, ProjectionName, "execution history must use storage time");

		var startedAt = now.AddSeconds(1);
		await storage.SetExecutionTelemetryAsync(
			job.JobHandle,
			active.Attempt,
			"projection-worker",
			"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
			"bbbbbbbbbbbbbbbb",
			startedAt,
			cancellationToken
		);
		await storage.CompleteAsync(job.JobHandle, active.Attempt, "projection-worker", cancellationToken);

		var completed = await GetJobAsync(storage, job.JobHandle, ProjectionName, cancellationToken);
		ConformanceAssert.Equal(JobState.Succeeded, completed.State, ProjectionName, "completion must update the acquired job projection");
		ConformanceAssert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", completed.ExecutionTraceId, ProjectionName, "execution trace id must round-trip");
		ConformanceAssert.Equal("bbbbbbbbbbbbbbbb", completed.ExecutionSpanId, ProjectionName, "execution span id must round-trip");
		ConformanceAssert.Equal(startedAt, completed.ExecutionStartedAt, ProjectionName, "execution start time must round-trip");
		executions = await storage.QueryJobExecutionsAsync(job.JobHandle, new(), cancellationToken);
		var completedExecution = ConformanceAssert.NotNull(
			executions.SingleOrDefault(),
			ProjectionName,
			"completion must update the same retained execution"
		);
		ConformanceAssert.Equal(JobExecutionState.Succeeded, completedExecution.State, ProjectionName, "history must record success");
		ConformanceAssert.Equal(startedAt, completedExecution.ExecutionStartedAt, ProjectionName, "history must retain telemetry");
	}

	private static async ValueTask ClaimsOnceUnderContentionAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var replica = Replica(storage, ContentionName);
		var now = timeProvider.GetUtcNow();
		var job = Job("contention-exact", now);
		await storage.EnqueueAsync(job, cancellationToken);
		var contenders = Enumerable.Range(0, 12)
			.Select(index => replica.AcquireJobsAsync(
				[job.JobHandle],
				string.Create(CultureInfo.InvariantCulture, $"contention-worker-{index}"),
				TimeSpan.FromMinutes(1),
				cancellationToken
			).AsTask())
			.ToList();

		var results = await Task.WhenAll(contenders);
		var claims = results.SelectMany(static result => result).ToList();
		ConformanceAssert.Equal(
			1,
			claims.Count,
			ContentionName,
			"concurrent exact-id acquisitions must claim one invocation version at most once",
			$"job={job.JobHandle}"
		);
		ConformanceAssert.Equal(1, claims[0].Attempt, ContentionName, "contention must create only the first execution ordinal");
		var executions = await storage.QueryJobExecutionsAsync(job.JobHandle, new(), cancellationToken);
		ConformanceAssert.Equal(1, executions.Count, ContentionName, "contention must create one active execution-history row");
	}

	private static async ValueTask ReclaimsAndFencesStaleOwnerAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var replica = Replica(storage, StaleName);
		var clock = timeProvider;
		var now = clock.GetUtcNow();
		var job = Job("stale-exact", now);
		await storage.EnqueueAsync(job, cancellationToken);
		var first = ConformanceAssert.NotNull(
			(await replica.AcquireJobsAsync(
				[job.JobHandle],
				"stale-worker-one",
				TimeSpan.FromMinutes(1),
				cancellationToken
			)).SingleOrDefault(),
			StaleName,
			"the initial owner must acquire the requested invocation"
		);
		clock.Advance(TimeSpan.FromMinutes(1));
		var second = ConformanceAssert.NotNull(
			(await replica.AcquireJobsAsync(
				[job.JobHandle],
				"stale-worker-two",
				TimeSpan.FromMinutes(2),
				cancellationToken
			)).SingleOrDefault(),
			StaleName,
			"an expired exact-id lease must be reclaimable"
		);
		ConformanceAssert.Equal(first.Attempt + 1, second.Attempt, StaleName, "reclaim must increment the execution ordinal");
		ConformanceAssert.Equal("stale-worker-two", second.WorkerId, StaleName, "reclaim must assign the new owner");

		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.CompleteAsync(job.JobHandle, first.Attempt, "stale-worker-one", cancellationToken),
			StaleName,
			"the stale replica owner must not complete the reclaimed execution"
		);
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => storage.RenewLeaseAsync(
				job.JobHandle,
				first.Attempt,
				"stale-worker-one",
				TimeSpan.FromMinutes(5),
				cancellationToken
			),
			StaleName,
			"the stale replica owner must not renew the reclaimed execution"
		);
		var persisted = await GetJobAsync(storage, job.JobHandle, StaleName, cancellationToken);
		ConformanceAssert.Equal(JobState.Active, persisted.State, StaleName, "a stale mutation must not alter the current execution");
		ConformanceAssert.Equal(second.Attempt, persisted.Attempt, StaleName, "a stale mutation must not alter the current ordinal");
		ConformanceAssert.Equal("stale-worker-two", persisted.WorkerId, StaleName, "a stale mutation must not alter the current owner");

		var executions = await storage.QueryJobExecutionsAsync(job.JobHandle, new(), cancellationToken);
		ConformanceAssert.SequenceEqual(
			[JobExecutionState.Active, JobExecutionState.Interrupted],
			executions.Select(static execution => execution.State),
			StaleName,
			"reclaim must interrupt the expired execution and retain the replacement as active"
		);
	}

	private static async ValueTask PreservesRecoveryFieldsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var replica = Replica(storage, FieldsName);
		var now = timeProvider.GetUtcNow();
		var job = Job("fields-exact", now) with
		{
			Payload = "{\"recovery\":true}",
			Context = "{\"tenant\":\"recovery\"}",
			GroupId = "recovery-group",
			RecurringKey = "recovery-schedule:123456789",
			TraceParent = "00-cccccccccccccccccccccccccccccccc-dddddddddddddddd-01",
			TraceState = "conformance=recovery",
		};
		if (storage is IJobGraphStorage graph)
		{
			var parent = Job("fields-parent", now) with { BatchHandle = BatchHandle.FromString("recovery-batch") };
			job = job with
			{
				BatchHandle = parent.BatchHandle,
				State = JobState.AwaitingContinuation,
				RemainingDependencies = 1,
			};
			await graph.EnqueueBatchAsync(
				new()
				{
					BatchHandle = job.BatchHandle,
					CreatedAt = now,
					TotalJobs = 2,
					PendingCount = 2,
					State = BatchState.Executing,
				},
				[parent, job],
				[
					new()
					{
						ChildJobHandle = job.JobHandle,
						ParentJobHandle = parent.JobHandle,
						Delay = TimeSpan.Zero,
						Trigger = ContinuationTrigger.Complete,
					},
				],
				cancellationToken
			);
			var activeParent = ConformanceAssert.NotNull(
				(await replica.AcquireJobsAsync(
					[parent.JobHandle],
					"fields-parent-worker",
					TimeSpan.FromMinutes(1),
					cancellationToken
				)).SingleOrDefault(),
				FieldsName,
				"the graph parent must be acquired before releasing the recovery record"
			);
			await storage.FailAsync(
				parent.JobHandle,
				activeParent.Attempt,
				"fields-parent-worker",
				"expected graph parent failure",
				nextRetryAt: null,
				cancellationToken
			);
			job = await GetJobAsync(storage, job.JobHandle, FieldsName, cancellationToken);
			ConformanceAssert.Equal(JobState.Pending, job.State, FieldsName, "a complete-trigger edge must release the recovery record");
			ConformanceAssert.Equal(0, job.RemainingDependencies, FieldsName, "the released record must have no remaining dependencies");
			ConformanceAssert.Equal(1, job.FailedDependencies, FieldsName, "the released record must retain its failed dependency count");
		}
		else
		{
			await storage.EnqueueAsync(job, cancellationToken);
		}

		var active = ConformanceAssert.NotNull(
			(await replica.AcquireJobsAsync(
				[job.JobHandle],
				"fields-worker",
				TimeSpan.FromMinutes(1),
				cancellationToken
			)).SingleOrDefault(),
			FieldsName,
			"the recovery record must be exactly acquired"
		);
		ConformanceAssert.Equal(job.Payload, active.Payload, FieldsName, "replica acquisition must preserve payload");
		ConformanceAssert.Equal(job.Context, active.Context, FieldsName, "replica acquisition must preserve ambient context");
		ConformanceAssert.Equal(job.GroupId, active.GroupId, FieldsName, "replica acquisition must preserve fair-queue group");
		ConformanceAssert.Equal(job.RecurringKey, active.RecurringKey, FieldsName, "replica acquisition must preserve recurring identity");
		ConformanceAssert.Equal(job.TraceParent, active.TraceParent, FieldsName, "replica acquisition must preserve trace parent");
		ConformanceAssert.Equal(job.TraceState, active.TraceState, FieldsName, "replica acquisition must preserve trace state");
		ConformanceAssert.Equal(job.BatchHandle, active.BatchHandle, FieldsName, "replica acquisition must preserve graph batch identity");
		ConformanceAssert.Equal(
			job.RemainingDependencies,
			active.RemainingDependencies,
			FieldsName,
			"replica acquisition must preserve remaining dependency count"
		);
		ConformanceAssert.Equal(
			job.FailedDependencies,
			active.FailedDependencies,
			FieldsName,
			"replica acquisition must preserve failed dependency count"
		);
	}

	private static IJobStorageReplica Replica(IJobStorage storage, string caseName) =>
		ConformanceAssert.IsAssignableFrom<IJobStorageReplica>(
			storage,
			caseName,
			"a storage advertising replica support must implement IJobStorageReplica"
		);

	private static JobRecord Job(string id, DateTimeOffset now) =>
		new()
		{
			QueueName = QueueName,
			JobHandle = JobHandle.FromString(id),
			JobName = JobName,
			Payload = "{\"source\":\"replica-conformance\"}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
		};

	private static async ValueTask<JobRecord> GetJobAsync(
		IJobStorage storage,
		string id,
		string caseName,
		CancellationToken cancellationToken
	)
	{
		var jobs = await storage.QueryJobsAsync(new() { JobHandle = JobHandle.FromString(id), Take = 10 }, cancellationToken);
		return ConformanceAssert.NotNull(
			jobs.SingleOrDefault(),
			caseName,
			"the expected exact-id job must be durably queryable",
			$"job={id}"
		);
	}

	private static ValueTask<JobRecord> GetJobAsync(
		IJobStorage storage,
		JobHandle id,
		string caseName,
		CancellationToken cancellationToken
	) => GetJobAsync(storage, id.Value, caseName, cancellationToken);

	private static void AssertAcquired(
		JobRecord expected,
		JobRecord actual,
		string workerId,
		DateTimeOffset leaseExpiresAt,
		string caseName
	)
	{
		ConformanceAssert.Equal(expected.JobHandle, actual.JobHandle, caseName, "exact acquisition must preserve the requested id");
		ConformanceAssert.Equal(expected.QueueName, actual.QueueName, caseName, "exact acquisition must preserve the queue");
		ConformanceAssert.Equal(expected.JobName, actual.JobName, caseName, "exact acquisition must preserve the job name");
		ConformanceAssert.Equal(expected.Payload, actual.Payload, caseName, "exact acquisition must preserve the payload");
		ConformanceAssert.Equal(expected.Context, actual.Context, caseName, "exact acquisition must preserve ambient context");
		ConformanceAssert.Equal(expected.GroupId, actual.GroupId, caseName, "exact acquisition must preserve the group id");
		ConformanceAssert.Equal(expected.TraceParent, actual.TraceParent, caseName, "exact acquisition must preserve trace parent");
		ConformanceAssert.Equal(expected.TraceState, actual.TraceState, caseName, "exact acquisition must preserve trace state");
		ConformanceAssert.Equal(JobState.Active, actual.State, caseName, "exact acquisition must project active state");
		ConformanceAssert.Equal(1, actual.Attempt, caseName, "exact acquisition must increment the attempt");
		ConformanceAssert.Equal(workerId, actual.WorkerId, caseName, "exact acquisition must project its owner");
		ConformanceAssert.Equal(leaseExpiresAt, actual.LeaseExpiresAt, caseName, "exact acquisition must project its lease expiry");
		ConformanceAssert.Null(actual.ExecutionTraceId, caseName, "a new execution must clear prior trace telemetry");
		ConformanceAssert.Null(actual.ExecutionSpanId, caseName, "a new execution must clear prior span telemetry");
		ConformanceAssert.Null(actual.ExecutionStartedAt, caseName, "a new execution must clear prior start telemetry");
	}
}

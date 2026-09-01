using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class RecurringStorageConformance
{
	private const string CapabilityName = "Recurring.Capability.ResolvesAdvertisedStorage";
	private const string LifecycleName = "Recurring.Lifecycle.UpdatesPausesResumesAndRemovesDynamicSchedule";
	private const string MergeDefinitionsName = "Recurring.Definitions.MergesCodeDefinedSchedulesAndPreservesDynamicSchedules";
	private const string DueScanName = "Recurring.DueScanning.FiltersOrdersAndBatchesSchedules";
	private const string MaterializeName = "Recurring.Materialization.CreatesOccurrenceAndAdvancesScheduleAtomically";
	private const string ConcurrentName = "Recurring.Materialization.DeduplicatesConcurrentOccurrence";
	private const string DedupeAdvanceName = "Recurring.Materialization.DedupeHitStillAdvancesSchedule";
	private const string StaleName = "Recurring.Materialization.RejectsStaleDueEntry";
	private const string SkippedName = "Recurring.Materialization.PersistsSkippedOccurrence";
	private const string PurgeName = "Recurring.Maintenance.PurgeRemovesOccurrenceDedupeState";
	private const string ExceptionsName = "Recurring.Exceptions.DistinguishesMissingAndInvalidDashboardActions";
	private const string QueueName = "conformance-recurring";
	private const string JobName = "conformance-recurring-job";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(CapabilityName, StorageCapabilities.Recurring, ResolvesAdvertisedStorage),
		new(LifecycleName, StorageCapabilities.Recurring, DynamicLifecycleAsync),
		new(MergeDefinitionsName, StorageCapabilities.Recurring, MergesDefinitionsAsync, ExistingRecurringSchedules()),
		new(DueScanName, StorageCapabilities.Recurring, FiltersDueSchedulesAsync),
		new(MaterializeName, StorageCapabilities.Recurring, MaterializesAtomicallyAsync),
		new(ConcurrentName, StorageCapabilities.Recurring, DeduplicatesConcurrentOccurrenceAsync),
		new(DedupeAdvanceName, StorageCapabilities.Recurring, AdvancesAfterDedupeHitAsync),
		new(StaleName, StorageCapabilities.Recurring, RejectsStaleDueEntryAsync),
		new(SkippedName, StorageCapabilities.Recurring, PersistsSkippedOccurrenceAsync),
		new(PurgeName, StorageCapabilities.Recurring, PurgeRemovesDedupeStateAsync),
		new(ExceptionsName, StorageCapabilities.Recurring, UsesDashboardExceptionConventionsAsync),
	];

	private static ValueTask ResolvesAdvertisedStorage(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_ = Recurring(storage, CapabilityName);
		return ValueTask.CompletedTask;
	}

	private static async ValueTask DynamicLifecycleAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, LifecycleName);
		var now = timeProvider.GetUtcNow();
		var original = Schedule("dynamic-lifecycle", now, isCodeDefined: false);
		await recurring.UpsertRecurringAsync(original, cancellationToken);
		var updated = original with
		{
			JobName = "conformance-recurring-updated",
			Cron = "15 * * * *",
			TimeZone = "Europe/Vienna",
			NextRunAt = now.AddHours(2),
		};
		await recurring.UpsertRecurringAsync(updated, cancellationToken);

		var persisted = await GetScheduleAsync(storage, original.Name, LifecycleName, cancellationToken);
		AssertSchedule(updated, persisted, LifecycleName, "a dynamic upsert must update provider-neutral schedule fields");

		await recurring.PauseRecurringAsync(original.Name, cancellationToken);
		persisted = await GetScheduleAsync(storage, original.Name, LifecycleName, cancellationToken);
		ConformanceAssert.True(persisted.IsPaused, LifecycleName, "PauseRecurringAsync must persist the paused state");
		ConformanceAssert.Equal(
			0,
			(await recurring.GetDueRecurringAsync(now.AddDays(1), 10, cancellationToken)).Count,
			LifecycleName,
			"a paused schedule must not be returned by a due scan"
		);

		await recurring.ResumeRecurringAsync(original.Name, cancellationToken);
		persisted = await GetScheduleAsync(storage, original.Name, LifecycleName, cancellationToken);
		ConformanceAssert.False(persisted.IsPaused, LifecycleName, "ResumeRecurringAsync must clear the paused state");

		await recurring.RemoveRecurringAsync(original.Name, cancellationToken);
		ConformanceAssert.False(
			(await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring.Any(
				schedule => string.Equals(schedule.Name, original.Name, StringComparison.Ordinal)
			),
			LifecycleName,
			"RemoveRecurringAsync must remove a dynamic schedule"
		);
	}

	private static PersistedJobState ExistingRecurringSchedules()
	{
		var now = new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
		return new PersistedJobState
		{
			Jobs = [],
			Batches = [],
			Edges = [],
			RecurringSchedules =
			[
				Schedule("merge-update", now.AddHours(1), isCodeDefined: true) with
				{
					IsPaused = true,
					LastRunAt = now.AddHours(-1),
				},
				Schedule("merge-remove", now.AddHours(2), isCodeDefined: true),
				Schedule("merge-dynamic-to-static", now.AddHours(3), isCodeDefined: false),
				Schedule("merge-dynamic", now.AddHours(3), isCodeDefined: false),
			],
		};
	}

	private static async ValueTask MergesDefinitionsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, MergeDefinitionsName);
		var now = timeProvider.GetUtcNow();
		var persistedAt = new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
		var updatedDefinition = Schedule("merge-update", now.AddHours(4), isCodeDefined: true) with
		{
			JobName = "merge-updated-job",
			QueueName = "merge-updated-queue",
			Cron = "30 * * * *",
			TimeZone = "Europe/Vienna",
		};
		var insertedDefinition = Schedule("merge-insert", now.AddHours(5), isCodeDefined: true);
		var upgradeToStatic = Schedule("merge-dynamic-to-static", now.AddHours(3), isCodeDefined: true);

		await recurring.MergeRecurringSchedulesListAsync([updatedDefinition, insertedDefinition, upgradeToStatic], cancellationToken);

		var schedules = (await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring;
		var updated = schedules.Single(schedule => string.Equals(schedule.Name, "merge-update", StringComparison.Ordinal));
		ConformanceAssert.Equal(updatedDefinition.JobName, updated.JobName, MergeDefinitionsName, "merge must update the job name");
		ConformanceAssert.Equal(updatedDefinition.QueueName, updated.QueueName, MergeDefinitionsName, "merge must update the queue name");
		ConformanceAssert.Equal(updatedDefinition.Cron, updated.Cron, MergeDefinitionsName, "merge must update the cron expression");
		ConformanceAssert.Equal(updatedDefinition.TimeZone, updated.TimeZone, MergeDefinitionsName, "merge must update the time zone");
		ConformanceAssert.Equal(updatedDefinition.NextRunAt, updated.NextRunAt, MergeDefinitionsName, "merge must update the next run time");
		ConformanceAssert.True(
			updated.IsPaused,
			MergeDefinitionsName,
			"merge must preserve an administratively paused state"
		);
		ConformanceAssert.Equal(persistedAt.AddHours(-1), updated.LastRunAt, MergeDefinitionsName, "merge must preserve the last run time");

		var names = schedules
			.Select(static schedule => schedule.Name)
			.Order(StringComparer.Ordinal);
		ConformanceAssert.SequenceEqual(
			new[] { updatedDefinition.Name, insertedDefinition.Name, "merge-dynamic", upgradeToStatic.Name }.Order(StringComparer.Ordinal),
			names,
			MergeDefinitionsName,
			"merge must insert new definitions, remove obsolete definitions, and preserve dynamic schedules"
		);
		var dynamic = schedules.Single(schedule => string.Equals(schedule.Name, "merge-dynamic", StringComparison.Ordinal));
		AssertSchedule(
			Schedule("merge-dynamic", persistedAt.AddHours(3), isCodeDefined: false),
			dynamic,
			MergeDefinitionsName,
			"merge must leave existing dynamic schedules untouched"
		);

		AssertSchedule(
			upgradeToStatic,
			schedules.Single(schedule => string.Equals(schedule.Name, "merge-dynamic-to-static", StringComparison.Ordinal)),
			MergeDefinitionsName,
			"merge must upgrade to static safely"
		);
	}

	private static async ValueTask FiltersDueSchedulesAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, DueScanName);
		var now = timeProvider.GetUtcNow();
		var first = Schedule("due-first", now.AddMinutes(-2), isCodeDefined: false);
		var second = Schedule("due-second", now.AddMinutes(-1), isCodeDefined: false);
		var paused = Schedule("due-paused", now.AddMinutes(-3), isCodeDefined: false) with { IsPaused = true };
		var future = Schedule("due-future", now.AddMinutes(1), isCodeDefined: false);
		foreach (var schedule in new[] { future, second, paused, first })
			await recurring.UpsertRecurringAsync(schedule, cancellationToken);

		var firstPage = await recurring.GetDueRecurringAsync(now, 1, cancellationToken);
		ConformanceAssert.SequenceEqual(
			[first.Name],
			firstPage.Select(static schedule => schedule.Name),
			DueScanName,
			"a due scan must order by next occurrence and honor its batch size"
		);
		var allDue = await recurring.GetDueRecurringAsync(now, 10, cancellationToken);
		ConformanceAssert.SequenceEqual(
			[first.Name, second.Name],
			allDue.Select(static schedule => schedule.Name),
			DueScanName,
			"a due scan must exclude paused and future schedules"
		);
	}

	private static async ValueTask MaterializesAtomicallyAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, MaterializeName);
		var now = timeProvider.GetUtcNow();
		var schedule = Schedule("materialize-atomic", now, isCodeDefined: true);
		var nextRunAt = now.AddHours(1);
		var occurrence = Occurrence("materialize-atomic-job", schedule, JobState.Pending, now);
		await recurring.UpsertRecurringAsync(schedule, cancellationToken);

		var inserted = await recurring.MaterializeRecurringAsync(schedule, occurrence, nextRunAt, cancellationToken);
		ConformanceAssert.True(inserted, MaterializeName, "the current due occurrence must be materialized");
		var persistedJob = await GetJobAsync(storage, occurrence.JobHandle, MaterializeName, cancellationToken);
		ConformanceAssert.Equal(
			occurrence.RecurringKey,
			persistedJob.RecurringKey,
			MaterializeName,
			"materialization must persist the occurrence's deduplication key"
		);
		var persistedSchedule = await GetScheduleAsync(storage, schedule.Name, MaterializeName, cancellationToken);
		ConformanceAssert.Equal(now, persistedSchedule.LastRunAt, MaterializeName, "materialization must record the occurrence time");
		ConformanceAssert.Equal(nextRunAt, persistedSchedule.NextRunAt, MaterializeName, "materialization must advance the schedule");
	}

	// TODO: Use storage to force failing order of events
	private static async ValueTask DeduplicatesConcurrentOccurrenceAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, ConcurrentName);
		var now = timeProvider.GetUtcNow();
		var schedule = Schedule("materialize-concurrent", now, isCodeDefined: true);
		var nextRunAt = now.AddHours(1);
		await recurring.UpsertRecurringAsync(schedule, cancellationToken);
		var first = Occurrence("materialize-concurrent-a", schedule, JobState.Pending, now);
		var second = Occurrence("materialize-concurrent-b", schedule, JobState.Pending, now);

		var results = await Task.WhenAll(
			recurring.MaterializeRecurringAsync(schedule, first, nextRunAt, cancellationToken).AsTask(),
			recurring.MaterializeRecurringAsync(schedule, second, nextRunAt, cancellationToken).AsTask()
		);
		ConformanceAssert.Equal(
			1,
			results.Count(static inserted => inserted),
			ConcurrentName,
			"concurrent materialization calls must create the occurrence exactly once"
		);
		var jobs = await storage.QueryJobsAsync(new() { JobName = JobName, Take = 10 }, cancellationToken);
		ConformanceAssert.Equal(1, jobs.Count, ConcurrentName, "only one job may exist for a concurrent occurrence");
		var persisted = await GetScheduleAsync(storage, schedule.Name, ConcurrentName, cancellationToken);
		ConformanceAssert.Equal(nextRunAt, persisted.NextRunAt, ConcurrentName, "the schedule must advance exactly once");
	}

	private static async ValueTask AdvancesAfterDedupeHitAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, DedupeAdvanceName);
		var now = timeProvider.GetUtcNow();
		var schedule = Schedule("materialize-dedupe", now, isCodeDefined: true);
		var nextRunAt = now.AddHours(1);
		var original = Occurrence("materialize-dedupe-original", schedule, JobState.Pending, now);
		await recurring.UpsertRecurringAsync(schedule, cancellationToken);
		ConformanceAssert.True(
			await recurring.MaterializeRecurringAsync(schedule, original, nextRunAt, cancellationToken),
			DedupeAdvanceName,
			"the first occurrence must be materialized"
		);
		await recurring.UpsertRecurringAsync(schedule, cancellationToken);

		var duplicate = original with { JobHandle = JobHandle.FromString("materialize-dedupe-duplicate") };
		ConformanceAssert.False(
			await recurring.MaterializeRecurringAsync(schedule, duplicate, nextRunAt, cancellationToken),
			DedupeAdvanceName,
			"a retained occurrence key must reject a duplicate job"
		);
		ConformanceAssert.Null(
			await storage.GetJobStatusAsync(duplicate.JobHandle, cancellationToken),
			DedupeAdvanceName,
			"a deduplication hit must not leave a duplicate job"
		);
		var persisted = await GetScheduleAsync(storage, schedule.Name, DedupeAdvanceName, cancellationToken);
		ConformanceAssert.Equal(now, persisted.LastRunAt, DedupeAdvanceName, "a dedupe hit must record the handled occurrence");
		ConformanceAssert.Equal(nextRunAt, persisted.NextRunAt, DedupeAdvanceName, "a dedupe hit must still advance the schedule");
	}

	private static async ValueTask RejectsStaleDueEntryAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, StaleName);
		var now = timeProvider.GetUtcNow();
		var schedule = Schedule("materialize-stale", now, isCodeDefined: true);
		var nextRunAt = now.AddHours(1);
		await recurring.UpsertRecurringAsync(schedule, cancellationToken);
		ConformanceAssert.True(
			await recurring.MaterializeRecurringAsync(
				schedule,
				Occurrence("materialize-stale-current", schedule, JobState.Pending, now),
				nextRunAt,
				cancellationToken
			),
			StaleName,
			"the current occurrence must be materialized"
		);

		var staleJob = Occurrence("materialize-stale-replay", schedule, JobState.Pending, now);
		ConformanceAssert.False(
			await recurring.MaterializeRecurringAsync(schedule, staleJob, now.AddDays(1), cancellationToken),
			StaleName,
			"a stale due snapshot must not materialize another occurrence"
		);
		ConformanceAssert.Null(
			await storage.GetJobStatusAsync(staleJob.JobHandle, cancellationToken),
			StaleName,
			"a stale materialization must not insert a job"
		);
		var persisted = await GetScheduleAsync(storage, schedule.Name, StaleName, cancellationToken);
		ConformanceAssert.Equal(nextRunAt, persisted.NextRunAt, StaleName, "a stale materialization must not roll the schedule forward or backward");
	}

	private static async ValueTask PersistsSkippedOccurrenceAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, SkippedName);
		var now = timeProvider.GetUtcNow();
		var schedule = Schedule("materialize-skipped", now, isCodeDefined: true);
		var skipped = Occurrence("materialize-skipped-job", schedule, JobState.Skipped, now) with
		{
			LastError = "overlap policy skipped this occurrence",
			CompletedAt = now,
		};
		await recurring.UpsertRecurringAsync(schedule, cancellationToken);
		ConformanceAssert.True(
			await recurring.MaterializeRecurringAsync(schedule, skipped, now.AddHours(1), cancellationToken),
			SkippedName,
			"a skipped occurrence must still be durably materialized"
		);
		var persisted = await GetJobAsync(storage, skipped.JobHandle, SkippedName, cancellationToken);
		ConformanceAssert.Equal(JobState.Skipped, persisted.State, SkippedName, "the supplied skipped state must be preserved");
		ConformanceAssert.Equal(skipped.CompletedAt, persisted.CompletedAt, SkippedName, "the skipped completion time must be preserved");
		ConformanceAssert.Equal(skipped.LastError, persisted.LastError, SkippedName, "the skipped reason must be preserved");
	}

	private static async ValueTask PurgeRemovesDedupeStateAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, PurgeName);
		var now = timeProvider.GetUtcNow();
		var schedule = Schedule("materialize-purge", now, isCodeDefined: true);
		var original = Occurrence("materialize-purge-original", schedule, JobState.Pending, now);
		await recurring.UpsertRecurringAsync(schedule, cancellationToken);
		ConformanceAssert.True(
			await recurring.MaterializeRecurringAsync(schedule, original, now.AddHours(1), cancellationToken),
			PurgeName,
			"the occurrence used by the retention scenario must be inserted"
		);
		var acquired = await storage.AcquireDueJobsAsync(
			Acquisition("recurring-purge-worker", TimeSpan.FromMinutes(1)),
			cancellationToken
		);
		var active = ConformanceAssert.NotNull(
			acquired.SingleOrDefault(job => job.JobHandle == original.JobHandle),
			PurgeName,
			"the materialized occurrence must be acquirable before completion"
		);
		await storage.CompleteAsync(active.JobHandle, active.Attempt, "recurring-purge-worker", cancellationToken);
		timeProvider.Advance(TimeSpan.FromMilliseconds(1));
		await storage.PurgeJobsAsync(TimeSpan.Zero, TimeSpan.Zero, cancellationToken);
		ConformanceAssert.Null(
			await storage.GetJobStatusAsync(original.JobHandle, cancellationToken),
			PurgeName,
			"retention cleanup must delete the completed occurrence"
		);

		await recurring.UpsertRecurringAsync(schedule, cancellationToken);
		var replacement = original with { JobHandle = JobHandle.FromString("materialize-purge-replacement") };
		ConformanceAssert.True(
			await recurring.MaterializeRecurringAsync(schedule, replacement, now.AddHours(1), cancellationToken),
			PurgeName,
			"purging an occurrence must release its provider-owned deduplication key"
		);
	}

	private static async ValueTask UsesDashboardExceptionConventionsAsync(
		IJobStorage storage,
		FakeTimeProvider timeProvider,
		CancellationToken cancellationToken
	)
	{
		var recurring = Recurring(storage, ExceptionsName);
		var now = timeProvider.GetUtcNow();
		var codeDefined = Schedule("exceptions-code-defined", now.AddHours(1), isCodeDefined: true);
		await recurring.UpsertRecurringAsync(codeDefined, cancellationToken);

		_ = await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => recurring.PauseRecurringAsync("exceptions-missing", cancellationToken),
			ExceptionsName,
			"pausing a missing schedule must throw KeyNotFoundException"
		);
		_ = await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => recurring.ResumeRecurringAsync("exceptions-missing", cancellationToken),
			ExceptionsName,
			"resuming a missing schedule must throw KeyNotFoundException"
		);
		_ = await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => recurring.RemoveRecurringAsync("exceptions-missing", cancellationToken),
			ExceptionsName,
			"removing a missing schedule must throw KeyNotFoundException"
		);
		_ = await ConformanceAssert.ThrowsAsync<ImmediateJobException>(
			() => recurring.RemoveRecurringAsync(codeDefined.Name, cancellationToken),
			ExceptionsName,
			"removing a code-defined schedule must throw ImmediateJobException"
		);
	}

	private static IRecurringJobStorage Recurring(IJobStorage storage, string caseName) =>
		ConformanceAssert.IsAssignableFrom<IRecurringJobStorage>(
			storage,
			caseName,
			"a storage advertising recurring support must implement IRecurringJobStorage"
		);

	private static RecurringJobSchedule Schedule(string name, DateTimeOffset nextRunAt, bool isCodeDefined) => new()
	{
		Name = name,
		JobName = JobName,
		QueueName = QueueName,
		Cron = "0 * * * *",
		TimeZone = "UTC",
		IsCodeDefined = isCodeDefined,
		NextRunAt = nextRunAt,
	};

	private static JobRecord Occurrence(
		string id,
		RecurringJobSchedule schedule,
		JobState state,
		DateTimeOffset now
	) => new()
	{
		QueueName = QueueName,
		JobHandle = JobHandle.FromString(id),
		JobName = JobName,
		Payload = "{\"source\":\"recurring-conformance\"}",
		Context = "{\"tenant\":\"recurring-conformance\"}",
		State = state,
		DueAt = schedule.NextRunAt,
		CreatedAt = now,
		RecurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{schedule.NextRunAt.UtcTicks}"),
		TraceParent = "00-11111111111111111111111111111111-2222222222222222-01",
		TraceState = "conformance=recurring",
	};

	private static JobAcquisitionRequest Acquisition(string workerId, TimeSpan lease) => new()
	{
		WorkerId = workerId,
		Lease = lease,
		BatchSize = 1,
		Queues =
		[
			new()
			{
				QueueName = QueueName,
				Capacity = 1,
				JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal) { [JobName] = 1 },
			},
		],
	};

	private static async ValueTask<RecurringJobSchedule> GetScheduleAsync(
		IJobStorage storage,
		string name,
		string caseName,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		return ConformanceAssert.NotNull(
			snapshot.Recurring.SingleOrDefault(schedule => string.Equals(schedule.Name, name, StringComparison.Ordinal)),
			caseName,
			"the expected recurring schedule must be present in monitoring",
			$"schedule={name}"
		);
	}

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
			"the expected recurring occurrence must be durably queryable",
			$"job={id}"
		);
	}

	private static ValueTask<JobRecord> GetJobAsync(
		IJobStorage storage,
		JobHandle id,
		string caseName,
		CancellationToken cancellationToken
	) => GetJobAsync(storage, id.Value, caseName, cancellationToken);

	private static void AssertSchedule(
		RecurringJobSchedule expected,
		RecurringJobSchedule actual,
		string caseName,
		string invariant
	)
	{
		ConformanceAssert.Equal(expected.Name, actual.Name, caseName, invariant, "field=Name");
		ConformanceAssert.Equal(expected.JobName, actual.JobName, caseName, invariant, "field=JobName");
		ConformanceAssert.Equal(expected.Cron, actual.Cron, caseName, invariant, "field=Cron");
		ConformanceAssert.Equal(expected.TimeZone, actual.TimeZone, caseName, invariant, "field=TimeZone");
		ConformanceAssert.Equal(expected.IsCodeDefined, actual.IsCodeDefined, caseName, invariant, "field=IsCodeDefined");
		ConformanceAssert.Equal(expected.IsPaused, actual.IsPaused, caseName, invariant, "field=IsPaused");
		ConformanceAssert.Equal(expected.NextRunAt, actual.NextRunAt, caseName, invariant, "field=NextRunAt");
		ConformanceAssert.Equal(expected.LastRunAt, actual.LastRunAt, caseName, invariant, "field=LastRunAt");
	}
}

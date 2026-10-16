using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class DefinitionCatalogStorageConformance
{
	private const string SubmissionCase = "Definitions.Submission.UsesCurrentDefinition";
	private const string RecurringCase = "Definitions.Recurring.PreservesProgressAndInvocations";
	private const string ConcurrentCase = "Definitions.Catalogue.SerializesConcurrentCompleteLists";
	private const string InvalidCase = "Definitions.Catalogue.RejectsInvalidSnapshotsWithoutPartialChanges";
	private const string CaseNameCase = "Definitions.Catalogue.ReconcilesCaseInsensitiveNames";
	private const string ConfigurationCase = "Definitions.Catalogue.RoundTripsAndUpdatesConfiguration";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(SubmissionCase, StorageCapabilities.Queue, UsesCurrentDefinitionAsync),
		new(RecurringCase, StorageCapabilities.Queue, PreservesRecurringAsync),
		new(ConcurrentCase, StorageCapabilities.Queue, ReconcilesConcurrentlyAsync),
		new(InvalidCase, StorageCapabilities.Queue, RejectsInvalidAsync),
		new(CaseNameCase, StorageCapabilities.Queue, ReconcilesCaseInsensitiveNamesAsync),
		new(ConfigurationCase, StorageCapabilities.Queue, RoundTripsConfigurationAsync),
	];

	private static async ValueTask UsesCurrentDefinitionAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var definition = Definition("Résumé") with { Queue = new() { Name = "priority" } };
		await storage.MergeJobDefinitionsListAsync(Registration([definition]), token);
		var job = new JobSubmission
		{
			JobHandle = JobHandle.FromString("resolved-job"),
			JobName = "RéSUMé",
			Payload = "{\"value\":\"routing\"}",
			GroupId = "tenant",
			State = JobState.Pending,
			DueAt = clock.GetUtcNow(),
			CreatedAt = clock.GetUtcNow(),
		};
		await storage.EnqueueAsync(job, token);
		var persisted = (await storage.QueryJobsAsync(new() { JobHandle = job.JobHandle }, token)).Single();
		ConformanceAssert.Equal(new JobRecord { JobHandle = job.JobHandle, JobName = definition.Name, QueueName = definition.Queue.Name, Payload = job.Payload, GroupId = job.GroupId, State = job.State, DueAt = job.DueAt, CreatedAt = job.CreatedAt }, persisted,
			SubmissionCase, "reads must expose the canonical name and current queue while preserving invocation data");

		var schedule = new RecurringJobSchedule
		{
			Name = "dynamic-routing",
			JobName = "RéSUMé",
			Cron = "* * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = clock.GetUtcNow().AddMinutes(1),
		};
		await storage.UpsertRecurringAsync(schedule, token);
		var persistedSchedule = (await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Single();
		ConformanceAssert.Equal(schedule with { JobName = definition.Name, QueueName = definition.Queue.Name }, persistedSchedule,
			SubmissionCase, "dynamic schedules must use the persisted definition without a caller-supplied queue");

		await storage.MergeJobDefinitionsListAsync(Registration([definition with { Queue = definition.Queue with { Name = "PRIORITY" } }]), token);
		ConformanceAssert.Equal("PRIORITY", (await storage.QueryJobsAsync(new() { JobHandle = job.JobHandle }, token)).Single().QueueName,
			SubmissionCase, "case-only queue changes must update persisted routing even on case-insensitive databases");
		await storage.MergeJobDefinitionsListAsync(Registration([definition with { Queue = definition.Queue with { Name = "changed" } }]), token);
		await storage.EnqueueAsync(job with { JobHandle = JobHandle.FromString("changed-queue"), State = JobState.WaitingForTrigger }, token);
		ConformanceAssert.Equal("changed", (await storage.QueryJobsAsync(new() { State = JobState.WaitingForTrigger }, token)).Single().QueueName,
			SubmissionCase, "later submissions must use the updated definition");
		var acquired = await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "routing-worker",
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = 1,
			Queues = [new() { QueueName = "changed", Capacity = 1, JobCapacities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [definition.Name] = 1 } }],
		}, token);
		ConformanceAssert.Equal(job.JobHandle, acquired.Single().JobHandle, SubmissionCase,
			"existing invocations must follow the current definition queue");

		await storage.MergeJobDefinitionsListAsync(Registration([]), token);
		ConformanceAssert.Null((await storage.QueryJobsAsync(new() { JobHandle = job.JobHandle }, token)).Single().QueueName,
			SubmissionCase, "retained invocations without a definition must have no queue");
		var status = ConformanceAssert.NotNull(await storage.GetJobStatusAsync(job.JobHandle, token),
			SubmissionCase, "the retained invocation must have a status");
		ConformanceAssert.Null(status.QueueName,
			SubmissionCase, "monitoring must retain jobs without inventing a queue");
		ConformanceAssert.Null((await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Single().QueueName,
			SubmissionCase, "retained schedules without a definition must have no queue");

		await ConformanceAssert.ThrowsAsync<KeyNotFoundException>(
			() => storage.EnqueueAsync(job with { JobHandle = JobHandle.FromString("missing-definition") }, token),
			SubmissionCase, "missing definitions must reject submission");

		ConformanceAssert.Equal(2, (await storage.QueryJobsAsync(new(), token)).Count, SubmissionCase, "a rejected submission must not write an invocation");
		ConformanceAssert.Equal(1, (await storage.GetMonitoringSnapshotAsync(token)).Recurring.Count, SubmissionCase, "definition removal retains the existing schedule");
	}

	private static JobDefinition Definition(string name) => new() { Name = name };

	private static JobDefinitionRegistration Registration(IReadOnlyList<JobDefinition> definitions) =>
		new() { Definitions = definitions };

	private static async ValueTask RoundTripsConfigurationAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		var definition = Definition("Configured") with
		{
			Tags = [JobTags.Default, "configuration"],
			Queue = new() { Name = "priority", Priority = 9, Concurrency = 2 },
			Cron = "15 * * * *",
			TimeZone = "America/Chicago",
			MaxAttempts = 7,
			Timeout = TimeSpan.FromSeconds(3) + TimeSpan.FromTicks(1),
			MaxConcurrency = 4,
			OverlapPolicy = OverlapPolicy.Concurrent,
			MisfireHandlingMode = MisfireHandlingMode.EnqueueNone,
			Backoff = BackoffStrategy.Exponential,
			BackoffBase = TimeSpan.FromSeconds(13) + TimeSpan.FromTicks(3),
			SlidingWindowMax = 5,
			SlidingWindowPeriod = TimeSpan.FromSeconds(17) + TimeSpan.FromTicks(5),
			FixedWindowMax = 6,
			FixedWindowPeriod = TimeSpan.FromSeconds(19) + TimeSpan.FromTicks(7),
		};
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [definition],
			RecurringSchedules = [Schedule(definition, timeProvider.GetUtcNow())],
		}, token);
		var stored = (await storage.GetJobDefinitionsAsync(token)).Single();
		ConformanceAssert.SequenceEqual(definition.Tags, stored.Tags, ConfigurationCase, "tag arrays must round trip independently of scalar configuration");
		ConformanceAssert.Equal(Values(definition), Values(stored), ConfigurationCase,
			"all configuration fields, nullable values, enums, and duration ticks must round trip");
		await storage.MergeJobDefinitionsListAsync(Registration([Definition("CONFIGURED")]), token);
		stored = (await storage.GetJobDefinitionsAsync(token)).Single();
		ConformanceAssert.SequenceEqual([JobTags.Default], stored.Tags, ConfigurationCase, "an update must replace the stored tag array");
		ConformanceAssert.Equal(Values(Definition("Configured")), Values(stored), ConfigurationCase,
			"an alias update must replace every field, clear nullable values, and preserve the stored name");

		static object Values(JobDefinition value) =>
			(value.Name, value.Queue.Name, value.Queue.Priority, value.Queue.Concurrency, value.Cron, value.TimeZone,
				value.MaxAttempts, value.Timeout, value.MaxConcurrency, value.OverlapPolicy, value.MisfireHandlingMode,
				value.Backoff, value.BackoffBase, value.SlidingWindowMax, value.SlidingWindowPeriod,
				value.FixedWindowMax, value.FixedWindowPeriod);
	}

	private static async ValueTask ReconcilesCaseInsensitiveNamesAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		var original = new[] { Definition("Invoice") with { Queue = new() { Name = "priority", Priority = 9, Concurrency = 2 }, Timeout = TimeSpan.FromMinutes(3), MaxConcurrency = 4, SlidingWindowMax = 3, SlidingWindowPeriod = TimeSpan.FromSeconds(5) + TimeSpan.FromTicks(3), FixedWindowMax = 12, FixedWindowPeriod = TimeSpan.FromMinutes(1), BackoffBase = TimeSpan.FromSeconds(13) }, Definition("Résumé"), Definition("Δοκιμή"), Definition("測試"), Definition(new string('a', 256)) };
		await storage.MergeJobDefinitionsListAsync(Registration(original), token);
		var aliases = original.Zip(["INVOICE", "RéSUMé", "Δοκιμή", "測試", new string('A', 256)],
			static (definition, name) => definition with { Name = name, MaxAttempts = 9 }).ToList();
		await storage.MergeJobDefinitionsListAsync(Registration(aliases), token);
		var definitions = await storage.GetJobDefinitionsAsync(token);
		var invoice = definitions.Single(static definition => string.Equals(definition.Name, "Invoice", StringComparison.Ordinal));
		ConformanceAssert.Equal(("priority", 9, 2, TimeSpan.FromMinutes(3), 4, TimeSpan.FromSeconds(13)),
			(invoice.Queue.Name, invoice.Queue.Priority, invoice.Queue.Concurrency, invoice.Timeout, invoice.MaxConcurrency, invoice.BackoffBase), CaseNameCase, "non-runtime metadata must round trip");
		ConformanceAssert.Equal(original[0].AcquisitionLimits, invoice.AcquisitionLimits, CaseNameCase, "remote monitoring and resume must use the persisted acquisition limits");
		ConformanceAssert.Equal(5, definitions.Count, CaseNameCase, "ASCII case variants must update the same definitions, preserving Unicode characters and maximum-length names");
		ConformanceAssert.True(definitions.All(static definition => definition.MaxAttempts == 9), CaseNameCase, "updates must reach case-insensitive identities");
		ConformanceAssert.SequenceEqual(original.Select(static definition => definition.Name).Order(StringComparer.OrdinalIgnoreCase),
			definitions.Select(static definition => definition.Name), CaseNameCase, "registration aliases must preserve the original stored spelling");
		await ConformanceAssert.ThrowsAsync<ArgumentException>(() => storage.MergeJobDefinitionsListAsync(Registration([Definition("Invoice"), Definition("invoice")]), token),
			CaseNameCase, "one catalogue cannot contain duplicate names that differ only by case");
		await storage.MergeJobDefinitionsListAsync(Registration([]), token);
		ConformanceAssert.Equal(0, (await storage.GetJobDefinitionsAsync(token)).Count, CaseNameCase, "an empty catalogue must remove aliases as one identity");
	}

	private static RecurringJobSchedule Schedule(JobDefinition definition, DateTimeOffset next) => new()
	{
		Name = definition.Name,
		JobName = definition.Name,
		QueueName = definition.Queue.Name,
		Cron = definition.Cron!,
		TimeZone = definition.TimeZone,
		IsCodeDefined = true,
		NextRunAt = next,
	};

	private static async ValueTask PreservesRecurringAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		var now = timeProvider.GetUtcNow();
		var ordinary = Definition("ordinary-cron") with { Cron = "* * * * *" };
		var schedule = Schedule(ordinary, now);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [ordinary], RecurringSchedules = [schedule] }, token);
		var unregistered = Schedule(Definition("unregistered") with { Cron = "* * * * *" }, now);
		await storage.UpsertRecurringAsync(unregistered, token);
		var dynamic = Schedule(ordinary, now) with { Name = "dynamic", IsCodeDefined = false };
		await storage.UpsertRecurringAsync(dynamic, token);
		var invocation = new JobRecord
		{
			JobHandle = JobHandle.FromString("retained-cron-invocation"),
			JobName = ordinary.Name,
			QueueName = ordinary.Queue.Name,
			State = JobState.Pending,
			Payload = "{}",
			CreatedAt = now,
			DueAt = now,
			RecurringKey = "ordinary-cron-occurrence",
		};
		ConformanceAssert.True(await storage.MaterializeRecurringAsync(schedule, invocation, now.AddMinutes(1), cancellationToken: token), RecurringCase, "the initial occurrence must materialize");
		await storage.PauseRecurringAsync(ordinary.Name, token);
		var alias = ordinary with { Name = "ORDINARY-CRON" };
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [alias], RecurringSchedules = [Schedule(alias, now.AddHours(1))] }, token);
		var persisted = (await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Single(item => string.Equals(item.Name, ordinary.Name, StringComparison.Ordinal));
		ConformanceAssert.False((await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Any(item => string.Equals(item.Name, unregistered.Name, StringComparison.Ordinal)),
			RecurringCase, "startup must clean up obsolete unregistered code schedules without metadata");
		ConformanceAssert.True(persisted.IsPaused, RecurringCase, "startup must preserve schedule pause state");
		ConformanceAssert.Equal(now.AddMinutes(1), persisted.NextRunAt, RecurringCase, "unchanged cron must retain progress");
		ConformanceAssert.Equal<DateTimeOffset?>(now, persisted.LastRunAt, RecurringCase, "startup must retain the last occurrence");
		var changed = ordinary with { Cron = "15 * * * *" };
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [changed], RecurringSchedules = [Schedule(changed, now.AddHours(2))] }, token);
		persisted = (await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Single(item => string.Equals(item.Name, ordinary.Name, StringComparison.Ordinal));
		ConformanceAssert.Equal(now.AddHours(2), persisted.NextRunAt, RecurringCase, "changed cron must reset the next occurrence");
		ConformanceAssert.True(persisted.IsPaused, RecurringCase, "cron changes must retain pause state");
		var reformatted = changed with { Cron = "15 * * * * " };
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [reformatted], RecurringSchedules = [Schedule(reformatted, now.AddHours(3))] }, token);
		persisted = (await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Single(item => string.Equals(item.Name, ordinary.Name, StringComparison.Ordinal));
		ConformanceAssert.Equal(now.AddHours(3), persisted.NextRunAt, RecurringCase, "any change to the stored cron text must reset the next occurrence");
		await storage.MergeJobDefinitionsListAsync(Registration([changed with { Cron = null }]), token);
		var snapshot = await storage.GetMonitoringSnapshotAsync(token);
		ConformanceAssert.SequenceEqual(["dynamic"], snapshot.Recurring.Select(static item => item.Name).Order(StringComparer.Ordinal), RecurringCase, "removing cron must preserve dynamic schedules");
		await storage.MergeJobDefinitionsListAsync(Registration([]), token);
		ConformanceAssert.NotNull(await storage.GetJobStatusAsync(invocation.JobHandle, token), RecurringCase, "removing metadata must never delete invocations");
	}

	private static async ValueTask ReconcilesConcurrentlyAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		var firstCron = Definition("a") with { Cron = "* * * * *" };
		var secondCron = Definition("c") with { Cron = "15 * * * *" };
		var first = new JobDefinitionRegistration
		{
			Definitions = [firstCron, Definition("b"), Definition("shared")],
			RecurringSchedules = [Schedule(firstCron, timeProvider.GetUtcNow())],
		};
		var second = new JobDefinitionRegistration
		{
			Definitions = [secondCron, Definition("d"), Definition("shared")],
			RecurringSchedules = [Schedule(secondCron, timeProvider.GetUtcNow())],
		};
		for (var round = 0; round < 5; round++)
		{
			await Task.WhenAll(
				storage.MergeJobDefinitionsListAsync(first, token).AsTask(),
				storage.MergeJobDefinitionsListAsync(second, token).AsTask(),
				storage.MergeJobDefinitionsListAsync(Registration([]), token).AsTask());
			var names = (await storage.GetJobDefinitionsAsync(token)).Select(static item => item.Name).ToList();
			ConformanceAssert.True(names.Count == 0 || names.SequenceEqual(["a", "b", "shared"], StringComparer.Ordinal) || names.SequenceEqual(["c", "d", "shared"], StringComparer.Ordinal),
				ConcurrentCase, "concurrent startup snapshots, including empty lists, must commit whole catalogues without mixed results");
			var schedules = await MonitoringTestQueries.GetSchedulesAsync(storage, token);
			ConformanceAssert.SequenceEqual(names.Where(static name => name is "a" or "c"), schedules.Select(static schedule => schedule.JobName),
				ConcurrentCase, "definitions and their code-defined schedules must commit together");
		}
	}

	private static async ValueTask RejectsInvalidAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		await storage.MergeJobDefinitionsListAsync(Registration([Definition("original")]), token);
		var invalid = Registration([Definition("missing-schedule") with { Cron = "* * * * *" }]);
		await ConformanceAssert.ThrowsAsync<ArgumentException>(() => storage.MergeJobDefinitionsListAsync(invalid, token), InvalidCase, "invalid catalogues must be rejected before mutation");
		foreach (var name in new[] { " original", "original ", "\toriginal", "original\n", "\u00a0original", "original\u00a0" })
		{
			await ConformanceAssert.ThrowsAsync<ArgumentException>(() => storage.MergeJobDefinitionsListAsync(Registration([Definition(name)]), token),
				InvalidCase, "names with leading or trailing whitespace must be rejected before mutation");
		}

		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();
		await ConformanceAssert.ThrowsAsync<OperationCanceledException>(() => storage.MergeJobDefinitionsListAsync(Registration([]), cancellation.Token), InvalidCase, "cancelled startup must not delete metadata");
		ConformanceAssert.SequenceEqual(["original"], (await storage.GetJobDefinitionsAsync(token)).Select(static item => item.Name), InvalidCase, "rejected calls must preserve the complete previous catalogue");
	}
}

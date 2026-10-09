using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class DefinitionCatalogStorageConformance
{
	private const string RecurringCase = "Definitions.Recurring.PreservesProgressAndInvocations";
	private const string ConcurrentCase = "Definitions.Catalogue.SerializesConcurrentCompleteLists";
	private const string InvalidCase = "Definitions.Catalogue.RejectsInvalidSnapshotsWithoutPartialChanges";
	private const string CaseNameCase = "Definitions.Catalogue.ReconcilesCaseInsensitiveNames";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(RecurringCase, StorageCapabilities.Queue, PreservesRecurringAsync),
		new(ConcurrentCase, StorageCapabilities.Queue, ReconcilesConcurrentlyAsync),
		new(InvalidCase, StorageCapabilities.Queue, RejectsInvalidAsync),
		new(CaseNameCase, StorageCapabilities.Queue, ReconcilesCaseInsensitiveNamesAsync),
	];

	private static JobDefinitionRecord Definition(string name) => new() { Name = name };

	private static JobDefinitionRegistration Registration(IReadOnlyList<JobDefinitionRecord> definitions) =>
		new() { Definitions = definitions };

	private static async ValueTask ReconcilesCaseInsensitiveNamesAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		var original = new[] { Definition("Invoice") with { QueueName = "priority", QueuePriority = 9, QueueConcurrency = 2, Timeout = TimeSpan.FromMinutes(3), MaxConcurrency = 4, BackoffBase = TimeSpan.FromSeconds(13) }, Definition("Résumé"), Definition(new string('a', 256)) };
		await storage.MergeJobDefinitionsListAsync(Registration(original), token);
		var aliases = original.Select(definition => definition with { Name = definition.Name.ToUpperInvariant(), MaxAttempts = 9 }).ToList();
		await storage.MergeJobDefinitionsListAsync(Registration(aliases), token);
		var definitions = await storage.GetJobDefinitionsAsync(token);
		var invoice = definitions.Single(static definition => string.Equals(definition.Name, "Invoice", StringComparison.Ordinal));
		ConformanceAssert.Equal(("priority", 9, 2, TimeSpan.FromMinutes(3), 4, TimeSpan.FromSeconds(13)),
			(invoice.QueueName, invoice.QueuePriority, invoice.QueueConcurrency, invoice.Timeout, invoice.MaxConcurrency, invoice.BackoffBase), CaseNameCase, "non-runtime metadata must round trip");
		ConformanceAssert.Equal(3, definitions.Count, CaseNameCase, "case variants must update the same definitions, including Unicode and maximum-length names");
		ConformanceAssert.True(definitions.All(static definition => definition.MaxAttempts == 9), CaseNameCase, "updates must reach case-insensitive identities");
		ConformanceAssert.SequenceEqual(original.Select(static definition => definition.Name).Order(StringComparer.OrdinalIgnoreCase),
			definitions.Select(static definition => definition.Name), CaseNameCase, "registration aliases must preserve the original stored spelling");
		await ConformanceAssert.ThrowsAsync<ArgumentException>(() => storage.MergeJobDefinitionsListAsync(Registration([Definition("Invoice"), Definition("invoice")]), token),
			CaseNameCase, "one catalogue cannot contain duplicate names that differ only by case");
		await storage.MergeJobDefinitionsListAsync(Registration([]), token);
		ConformanceAssert.Equal(0, (await storage.GetJobDefinitionsAsync(token)).Count, CaseNameCase, "an empty catalogue must remove aliases as one identity");
	}

	private static RecurringJobSchedule Schedule(JobDefinitionRecord definition, DateTimeOffset next) => new()
	{
		Name = definition.Name,
		JobName = definition.Name,
		QueueName = definition.QueueName,
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
		var legacy = Schedule(Definition("legacy") with { Cron = "* * * * *" }, now);
		await storage.UpsertRecurringAsync(legacy, token);
		var dynamic = Schedule(ordinary, now) with { Name = "dynamic", IsCodeDefined = false };
		await storage.UpsertRecurringAsync(dynamic, token);
		var invocation = new JobRecord
		{
			JobHandle = JobHandle.FromString("retained-cron-invocation"),
			JobName = ordinary.Name,
			QueueName = ordinary.QueueName,
			State = JobState.Pending,
			Payload = "{}",
			CreatedAt = now,
			DueAt = now,
			RecurringKey = "ordinary-cron-occurrence",
		};
		ConformanceAssert.True(await storage.MaterializeRecurringAsync(schedule, invocation, now.AddMinutes(1), cancellationToken: token), RecurringCase, "the initial occurrence must materialize");
		await storage.PauseRecurringAsync(ordinary.Name, token);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [ordinary], RecurringSchedules = [Schedule(ordinary, now.AddHours(1))] }, token);
		var persisted = (await storage.GetMonitoringSnapshotAsync(token)).Recurring.Single(item => string.Equals(item.Name, ordinary.Name, StringComparison.Ordinal));
		ConformanceAssert.False((await storage.GetMonitoringSnapshotAsync(token)).Recurring.Any(item => string.Equals(item.Name, legacy.Name, StringComparison.Ordinal)),
			RecurringCase, "startup must clean up obsolete legacy code schedules without metadata");
		ConformanceAssert.True(persisted.IsPaused, RecurringCase, "startup must preserve schedule pause state");
		ConformanceAssert.Equal(now.AddMinutes(1), persisted.NextRunAt, RecurringCase, "unchanged cron must retain progress");
		ConformanceAssert.Equal<DateTimeOffset?>(now, persisted.LastRunAt, RecurringCase, "startup must retain the last occurrence");
		var changed = ordinary with { Cron = "15 * * * *" };
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [changed], RecurringSchedules = [Schedule(changed, now.AddHours(2))] }, token);
		persisted = (await storage.GetMonitoringSnapshotAsync(token)).Recurring.Single(item => string.Equals(item.Name, ordinary.Name, StringComparison.Ordinal));
		ConformanceAssert.Equal(now.AddHours(2), persisted.NextRunAt, RecurringCase, "changed cron must reset the next occurrence");
		ConformanceAssert.True(persisted.IsPaused, RecurringCase, "cron changes must retain pause state");
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
			var schedules = (await storage.GetMonitoringSnapshotAsync(token)).Recurring;
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

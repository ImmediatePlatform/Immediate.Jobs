using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class TagStorageConformance
{
	private const string ScopeCase = "Tags.Catalogue.ReconcilesCompleteListsWithinTagScope";
	private const string RetagCase = "Tags.Catalogue.RemovesPreviouslyMatchingRetaggedDefinitions";
	private const string RecurringCase = "Tags.Recurring.PreservesOtherScopesProgressAndInvocations";
	private const string ConcurrentCase = "Tags.Catalogue.SerializesConcurrentCompleteLists";
	private const string DueCase = "Tags.Recurring.FiltersEligibleNamesBeforeBatchLimit";
	private const string CaseNameCase = "Tags.Catalogue.ReconcilesCaseInsensitiveNamesAcrossTagScopes";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(ScopeCase, StorageCapabilities.Queue, ReconcilesScopesAsync),
		new(RetagCase, StorageCapabilities.Queue, RetagsAsync),
		new(RecurringCase, StorageCapabilities.Queue, PreservesRecurringAsync),
		new(ConcurrentCase, StorageCapabilities.Queue, ReconcilesConcurrentlyAsync),
		new(DueCase, StorageCapabilities.Queue, FiltersBeforeBatchAsync),
		new(CaseNameCase, StorageCapabilities.Queue, ReconcilesCaseInsensitiveNamesAsync),
	];

	private static JobDefinition Definition(string name, params string[] tags) => new() { Name = name, Tags = tags };

	private static JobDefinitionRegistration Registration(IReadOnlyList<JobDefinition> definitions, params string[] tags) =>
		new() { Definitions = definitions, ServerTags = tags };

	private static async ValueTask ReconcilesCaseInsensitiveNamesAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		var original = new[] { Definition("Invoice"), Definition("Résumé"), Definition(new string('a', 256)) };
		await storage.MergeJobDefinitionsListAsync(Registration(original), token);
		var aliases = original.Zip(["INVOICE", "RéSUMé", new string('A', 256)],
			static (definition, name) => definition with { Name = name, MaxAttempts = 9 }).ToList();
		await storage.MergeJobDefinitionsListAsync(Registration(aliases), token);
		var definitions = await storage.GetJobDefinitionsAsync(token);
		ConformanceAssert.Equal(3, definitions.Count, CaseNameCase, "ASCII case variants must update the same definitions, preserving Unicode characters and maximum-length names");
		ConformanceAssert.True(definitions.All(static definition => definition.MaxAttempts == 9), CaseNameCase, "updates must reach case-insensitive identities");
		ConformanceAssert.SequenceEqual(original.Select(static definition => definition.Name).Order(StringComparer.OrdinalIgnoreCase),
			definitions.Select(static definition => definition.Name), CaseNameCase, "registration aliases must preserve the original stored spelling");
		await storage.MergeJobDefinitionsListAsync(Registration(aliases.Select(definition => definition with { Tags = ["email"] }).ToList(), "email"), token);
		await storage.MergeJobDefinitionsListAsync(Registration([]), token);
		ConformanceAssert.Equal(3, (await storage.GetJobDefinitionsAsync(token)).Count, CaseNameCase, "default cleanup must preserve retagged definitions despite name casing");
		await ConformanceAssert.ThrowsAsync<ArgumentException>(() => storage.MergeJobDefinitionsListAsync(Registration([Definition("Invoice"), Definition("invoice")]), token),
			CaseNameCase, "one catalogue cannot contain duplicate names that differ only by case");
		await storage.MergeJobDefinitionsListAsync(Registration([], "email"), token);
		ConformanceAssert.Equal(0, (await storage.GetJobDefinitionsAsync(token)).Count, CaseNameCase, "the matching tag scope must remove aliases as one identity");
	}

	private static async ValueTask ReconcilesScopesAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		var ordinary = Definition("ordinary");
		var other = Definition("email", "email");
		var shared = Definition("shared", "default", "email");
		await storage.MergeJobDefinitionsListAsync(Registration([ordinary, other, shared]), token);
		ConformanceAssert.SequenceEqual(["ordinary", "shared"], (await storage.GetJobDefinitionsAsync(token)).Select(static definition => definition.Name), ScopeCase, "a default server must ignore non-default definitions");
		await storage.MergeJobDefinitionsListAsync(Registration([ordinary, other, shared], "email"), token);
		var updated = ordinary with
		{
			QueueName = "priority",
			QueuePriority = 9,
			QueueConcurrency = 2,
			MaxAttempts = 8,
			MaxConcurrency = 4,
			Timeout = TimeSpan.FromMinutes(3),
			TimeZone = "Europe/Vienna",
			OverlapPolicy = OverlapPolicy.Concurrent,
			MisfireHandlingMode = MisfireHandlingMode.EnqueueAll,
			Backoff = BackoffStrategy.Fixed,
			BackoffBase = TimeSpan.FromSeconds(13),
		};
		var registration = Registration([updated, other]);
		await storage.MergeJobDefinitionsListAsync(registration, token);
		await storage.MergeJobDefinitionsListAsync(registration, token);
		var definitions = await storage.GetJobDefinitionsAsync(token);
		ConformanceAssert.SequenceEqual(["email", "ordinary"], definitions.Select(static definition => definition.Name), ScopeCase, "complete-list reconciliation must remove scoped omissions and preserve other scopes");
		var actual = definitions.Single(definition => string.Equals(definition.Name, ordinary.Name, StringComparison.Ordinal));
		ConformanceAssert.Equal(
			(updated.Name, updated.QueueName, updated.QueuePriority, updated.QueueConcurrency, updated.Cron, updated.TimeZone,
				updated.MaxAttempts, updated.MaxConcurrency, updated.Timeout, updated.OverlapPolicy, updated.MisfireHandlingMode, updated.Backoff, updated.BackoffBase),
			(actual.Name, actual.QueueName, actual.QueuePriority, actual.QueueConcurrency, actual.Cron, actual.TimeZone,
				actual.MaxAttempts, actual.MaxConcurrency, actual.Timeout, actual.OverlapPolicy, actual.MisfireHandlingMode, actual.Backoff, actual.BackoffBase),
			ScopeCase, "all non-runtime metadata must round trip and repeated startup must be idempotent");
		ConformanceAssert.SequenceEqual(["default"], actual.Tags, ScopeCase, "empty job tags must persist as default");
		await storage.MergeJobDefinitionsListAsync(Registration([], "DEFAULT"), token);
		ConformanceAssert.SequenceEqual(["email"], (await storage.GetJobDefinitionsAsync(token)).Select(static definition => definition.Name), ScopeCase, "an empty default catalogue must preserve the email scope");
		ConformanceAssert.SequenceEqual(["email"], (await storage.GetMonitoringDefinitionsAsync(token)).Jobs.Select(static definition => definition.Name), ScopeCase, "monitoring must include definitions outside the current server scope");
	}

	private static async ValueTask RetagsAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		var original = Definition("moving");
		await storage.MergeJobDefinitionsListAsync(Registration([original]), token);
		var retagged = original with { Tags = ["email"] };
		await storage.MergeJobDefinitionsListAsync(Registration([retagged]), token);
		ConformanceAssert.Equal(0, (await storage.GetJobDefinitionsAsync(token)).Count, RetagCase, "a definition retagged out of its former scope must not remain stale");
		await storage.MergeJobDefinitionsListAsync(Registration([retagged], "email"), token);
		await storage.MergeJobDefinitionsListAsync(Registration([], "email "), token);
		ConformanceAssert.Equal(1, (await storage.GetJobDefinitionsAsync(token)).Count, RetagCase, "case-insensitive tags must still distinguish trailing spaces");
		await storage.MergeJobDefinitionsListAsync(Registration([], "Email"), token);
		ConformanceAssert.Equal(0, (await storage.GetJobDefinitionsAsync(token)).Count, RetagCase, "tag case variants must manage the same scope");
		await storage.MergeJobDefinitionsListAsync(Registration([retagged], "EMAIL"), token);

		await storage.MergeJobDefinitionsListAsync(Registration([original]), token);
		ConformanceAssert.SequenceEqual(["default"], (await storage.GetJobDefinitionsAsync(token)).Single().Tags, RetagCase, "a newly matching supplied definition can replace previously out-of-scope metadata");
		var unicode = original with { Tags = ["Δοκιμή"] };
		await storage.MergeJobDefinitionsListAsync(Registration([unicode], "Δοκιμή"), token);
		await storage.MergeJobDefinitionsListAsync(Registration([], "δοκιμή"), token);
		ConformanceAssert.Equal(0, (await storage.GetJobDefinitionsAsync(token)).Count, RetagCase, "Unicode tag case variants must manage the same scope");
	}

	private static RecurringJobSchedule Schedule(JobDefinition definition, DateTimeOffset next) => new()
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
		var other = Definition("email-cron", "email") with { Cron = "* * * * *" };
		var schedule = Schedule(ordinary, now);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [ordinary], RecurringSchedules = [schedule] }, token);
		var unregistered = Schedule(Definition("unregistered") with { Cron = "* * * * *" }, now);
		await storage.UpsertRecurringAsync(unregistered, token);
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [other], ServerTags = ["email"], RecurringSchedules = [Schedule(other, now)] }, token);
		ConformanceAssert.True((await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Any(item => string.Equals(item.Name, unregistered.Name, StringComparison.Ordinal)),
			RecurringCase, "a server without default must preserve unregistered code schedules without metadata");
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
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [ordinary], ServerTags = ["DEFAULT"], RecurringSchedules = [Schedule(ordinary, now.AddHours(1))] }, token);
		var persisted = (await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Single(item => string.Equals(item.Name, ordinary.Name, StringComparison.Ordinal));
		ConformanceAssert.False((await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Any(item => string.Equals(item.Name, unregistered.Name, StringComparison.Ordinal)),
			RecurringCase, "a default server must clean up obsolete unregistered code schedules without metadata");
		ConformanceAssert.True(persisted.IsPaused, RecurringCase, "startup must preserve schedule pause state");
		ConformanceAssert.Equal(now.AddMinutes(1), persisted.NextRunAt, RecurringCase, "unchanged cron must retain progress");
		ConformanceAssert.Equal<DateTimeOffset?>(now, persisted.LastRunAt, RecurringCase, "startup must retain the last occurrence");
		var changed = ordinary with { Cron = "15 * * * *" };
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [changed], RecurringSchedules = [Schedule(changed, now.AddHours(2))] }, token);
		persisted = (await MonitoringTestQueries.GetSchedulesAsync(storage, token)).Single(item => string.Equals(item.Name, ordinary.Name, StringComparison.Ordinal));
		ConformanceAssert.Equal(now.AddHours(2), persisted.NextRunAt, RecurringCase, "changed cron must reset the next occurrence");
		ConformanceAssert.True(persisted.IsPaused, RecurringCase, "cron changes must retain pause state");
		await storage.MergeJobDefinitionsListAsync(Registration([changed with { Cron = null }]), token);
		var snapshot = await storage.GetMonitoringSnapshotAsync(token);
		ConformanceAssert.SequenceEqual(["dynamic", "email-cron"], snapshot.Recurring.Select(static item => item.Name).Order(StringComparer.Ordinal), RecurringCase, "removing cron must preserve dynamic and unrelated schedules");
		await storage.MergeJobDefinitionsListAsync(Registration([]), token);
		ConformanceAssert.NotNull(await storage.GetJobStatusAsync(invocation.JobHandle, token), RecurringCase, "removing metadata must never delete invocations");
	}

	private static async ValueTask ReconcilesConcurrentlyAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		await Task.WhenAll(
			storage.MergeJobDefinitionsListAsync(Registration([Definition("a"), Definition("b")]), token).AsTask(),
			storage.MergeJobDefinitionsListAsync(Registration([Definition("email", "email")], "email"), token).AsTask());
		ConformanceAssert.SequenceEqual(["a", "b", "email"], (await storage.GetJobDefinitionsAsync(token)).Select(static item => item.Name), ConcurrentCase, "disjoint startup scopes must not lose one another's definitions");
		await Task.WhenAll(
			storage.MergeJobDefinitionsListAsync(Registration([Definition("a"), Definition("b")]), token).AsTask(),
			storage.MergeJobDefinitionsListAsync(Registration([Definition("c"), Definition("d")]), token).AsTask());
		var scoped = (await storage.GetJobDefinitionsAsync(token)).Where(definition => definition.Tags.Contains("default", StringComparer.OrdinalIgnoreCase)).Select(static item => item.Name).ToList();
		ConformanceAssert.True(scoped.SequenceEqual(["a", "b"], StringComparer.Ordinal) || scoped.SequenceEqual(["c", "d"], StringComparer.Ordinal), ConcurrentCase, "overlapping startup snapshots must commit whole lists without mixed results");
	}

	private static async ValueTask FiltersBeforeBatchAsync(IJobStorage storage, FakeTimeProvider timeProvider, CancellationToken token)
	{
		var now = timeProvider.GetUtcNow();
		var ignored = Definition("ignored") with { Cron = "* * * * *" };
		await storage.MergeJobDefinitionsListAsync(Registration([
			ignored with { Cron = null }, Definition("eligible"), Definition("moved"), Definition("other-eligible"),
		]), token);

		for (var index = 0; index < 4; index++)
			await storage.UpsertRecurringAsync(Schedule(ignored, now.AddMinutes(-2)) with { Name = FormattableString.Invariant($"ignored-{index}"), IsCodeDefined = false }, token);
		var eligible = Definition("eligible") with { Cron = "* * * * *" };
		await storage.UpsertRecurringAsync(Schedule(eligible, now.AddMinutes(-1)) with { IsCodeDefined = false }, token);
		var schedules = await storage.GetDueRecurringAsync(now, 1, [eligible.Name], token);
		ConformanceAssert.SequenceEqual([eligible.Name], schedules.Select(static item => item.JobName), DueCase, "ineligible schedules must not occupy the batch");
		ConformanceAssert.Equal(0, (await storage.GetDueRecurringAsync(now, 1, [], token)).Count, DueCase, "an empty eligible-name list must return no schedules");

		await storage.PauseRecurringAsync(eligible.Name, token);
		ConformanceAssert.Equal(0, (await storage.GetDueRecurringAsync(now, 1, [eligible.Name], token)).Count, DueCase, "pause must remove eligibility");
		await storage.ResumeRecurringAsync(eligible.Name, token);
		ConformanceAssert.Equal(1, (await storage.GetDueRecurringAsync(now, 1, [eligible.Name], token)).Count, DueCase, "resume must restore eligibility");

		var moved = Schedule(eligible, now.AddMinutes(-1)) with { IsCodeDefined = false, JobName = "moved" };
		await storage.UpsertRecurringAsync(moved, token);
		ConformanceAssert.Equal(0, (await storage.GetDueRecurringAsync(now, 1, [eligible.Name], token)).Count, DueCase, "retargeting must remove the old job's eligibility");
		var second = moved with { Name = "second", JobName = "other-eligible", NextRunAt = now.AddSeconds(-30) };
		await storage.UpsertRecurringAsync(second, token);
		schedules = await storage.GetDueRecurringAsync(now, 1, [second.JobName, moved.JobName, moved.JobName], token);
		ConformanceAssert.SequenceEqual([moved.Name], schedules.Select(static item => item.Name), DueCase, "merge eligible jobs by due time before taking the batch, without duplicates");

		var job = new JobRecord
		{
			JobHandle = JobHandle.FromString("tagged-index-occurrence"),
			JobName = moved.JobName,
			QueueName = moved.QueueName,
			State = JobState.Pending,
			Payload = "{}",
			CreatedAt = now,
			DueAt = now,
			RecurringKey = "tagged-index-occurrence",
		};
		ConformanceAssert.True(await storage.MaterializeRecurringAsync(moved, job, now.AddMinutes(1), cancellationToken: token), DueCase, "materialization must advance the due index");
		ConformanceAssert.Equal(0, (await storage.GetDueRecurringAsync(now, 1, [moved.JobName], token)).Count, DueCase, "materialized schedules must leave the due batch");
		ConformanceAssert.Equal(1, (await storage.GetDueRecurringAsync(now.AddMinutes(1), 1, [moved.JobName], token)).Count, DueCase, "the advanced occurrence must retain its job eligibility");
		await storage.RemoveRecurringAsync(moved.Name, token);
		ConformanceAssert.Equal(0, (await storage.GetDueRecurringAsync(now.AddMinutes(1), 1, [moved.JobName], token)).Count, DueCase, "removal must clear job eligibility");

		var code = Definition("indexed-code") with { Cron = "* * * * *" };
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [code], RecurringSchedules = [Schedule(code, now)] }, token);
		ConformanceAssert.Equal(1, (await storage.GetDueRecurringAsync(now, 1, [code.Name], token)).Count, DueCase, "catalogue reconciliation must maintain job eligibility");
		await storage.MergeJobDefinitionsListAsync(Registration([]), token);
		ConformanceAssert.Equal(0, (await storage.GetDueRecurringAsync(now, 1, [code.Name], token)).Count, DueCase, "catalogue removal must clear job eligibility");
	}

}

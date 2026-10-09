using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal static class MonitoringStorageConformance
{
	private const string BulkCase = "Monitoring.Snapshot.BulkStatusMatchesIndividualReadsAtBoundaries";
	private const string MetadataCase = "Monitoring.Definitions.IncludesGlobalJobsAndDynamicRecurringConfiguration";

	internal static IReadOnlyList<JobStorageConformanceTestCase> Cases { get; } =
	[
		new(BulkCase, StorageCapabilities.Queue, BulkAsync),
		new(MetadataCase, StorageCapabilities.Queue, MetadataAsync),
	];

	private static async ValueTask BulkAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		var now = clock.GetUtcNow();
		JobDefinitionRecord[] definitions =
		[
			new()
			{
				Name = "Limited",
				SlidingWindowMax = 1,
				SlidingWindowPeriod = TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(3),
				FixedWindowMax = 1,
				FixedWindowPeriod = TimeSpan.FromSeconds(2),
				MaxConcurrency = 1,
			},
			new() { Name = "Other", SlidingWindowMax = 2, SlidingWindowPeriod = TimeSpan.FromMinutes(1) },
			new() { Name = "Idle" },
		];
		await storage.MergeJobDefinitionsListAsync(new() { Definitions = definitions }, token);
		foreach (var definition in definitions.Take(2))
			await storage.EnqueueAsync(new()
			{
				JobHandle = JobHandle.FromString("monitor-" + definition.Name),
				JobName = definition.Name,
				Payload = "{}",
				State = JobState.Pending,
				CreatedAt = now,
				DueAt = now,
			}, token);
		var acquired = await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "monitor",
			BatchSize = 2,
			Lease = TimeSpan.FromSeconds(1),
			Queues = [new() { QueueName = "default", Capacity = 2, JobCapacities = definitions.ToDictionary(static definition => definition.Name, static _ => 1, StringComparer.OrdinalIgnoreCase) }],
			JobLimits = definitions.ToDictionary(static definition => definition.Name, static definition => definition.AcquisitionLimits, StringComparer.OrdinalIgnoreCase),
		}, token);
		ConformanceAssert.Equal(2, acquired.Count, BulkCase, "the two eligible definitions acquire independently");
		await storage.PauseJobAsync("Other", token);
		foreach (var elapsed in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromTicks(3), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1) })
		{
			clock.Advance(elapsed);
			var snapshot = await storage.GetMonitoringSnapshotAsync(token);
			ConformanceAssert.Equal(3, snapshot.DefinitionStatuses.Count, BulkCase, "idle and restricted definitions remain visible");
			foreach (var definition in definitions)
			{
				var expected = await storage.GetJobAcquisitionStateAsync(definition.Name, definition.AcquisitionLimits, token);
				var actual = snapshot.DefinitionStatuses.Single(state => string.Equals(state.JobName, definition.Name, StringComparison.OrdinalIgnoreCase));
				ConformanceAssert.Equal(expected, actual, BulkCase, "bulk evaluation matches exact window and lease boundaries");
			}
		}

		await storage.MergeJobDefinitionsListAsync(new() { Definitions = [definitions[2]] }, token);
		ConformanceAssert.Equal("Idle", (await storage.GetMonitoringSnapshotAsync(token)).DefinitionStatuses.Single().JobName,
			BulkCase, "removed catalogue definitions do not leak retained acquisition history into monitoring");
	}

	private static async ValueTask MetadataAsync(IJobStorage storage, FakeTimeProvider clock, CancellationToken token)
	{
		await storage.MergeJobDefinitionsListAsync(new()
		{
			ServerTags = ["remote"],
			Definitions = [new() { Name = "Remote", Tags = ["remote"], QueueName = "priority" }],
		}, token);
		var schedule = new RecurringJobSchedule
		{
			Name = "dynamic",
			JobName = "Remote",
			QueueName = "priority",
			Cron = "* * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = clock.GetUtcNow().AddMinutes(1),
		};
		await storage.UpsertRecurringAsync(schedule, token);
		await storage.PauseRecurringAsync(schedule.Name, token);
		var metadata = await storage.GetMonitoringDefinitionsAsync(token);
		ConformanceAssert.Equal("Remote", metadata.Jobs.Single().Name, MetadataCase, "metadata reads are independent of local registration and tags");
		ConformanceAssert.Equal(schedule.ToDefinition(), metadata.Recurring.Single(), MetadataCase, "dynamic configuration is returned without pause and progress");
		ConformanceAssert.Equal(schedule.ToStatus() with { IsPaused = true }, (await storage.GetMonitoringSnapshotAsync(token)).Recurring.Single(), MetadataCase,
			"live recurring status carries pause and progress separately");
	}
}

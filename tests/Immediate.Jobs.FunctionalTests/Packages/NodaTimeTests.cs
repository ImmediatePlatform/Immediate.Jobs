using System.Text.Json;
using System.Text.Json.Serialization;
using Immediate.Jobs.NodaTime;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Immediate.Jobs.FunctionalTests.Packages;

#pragma warning disable CS1591
public sealed class NodaTimeTests
{
	[Fact]
	public async Task SchedulerOverloadsConvertDurationAndInstant()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var start = Instant.FromUtc(2026, 7, 20, 10, 0);
		var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(start.ToDateTimeOffset());
		var scheduler = new CaptureOnlyJobScheduler<SchedulerRequest>(clock);

		_ = await scheduler.ScheduleAsync(
			new("later"),
			Duration.FromMinutes(5),
			cancellationToken,
			groupId: "tenant-a"
		);
		_ = await scheduler.ScheduleAtAsync(
			new("absolute"),
			start + Duration.FromHours(2),
			cancellationToken,
			groupId: "tenant-b"
		);

		Assert.Equal(start + Duration.FromMinutes(5), Instant.FromDateTimeOffset(scheduler.Captures[0].RunAt));
		Assert.Equal(start + Duration.FromHours(2), Instant.FromDateTimeOffset(scheduler.Captures[1].RunAt));
		Assert.Equal("tenant-a", scheduler.Captures[0].GroupId);
		Assert.Equal("tenant-b", scheduler.Captures[1].GroupId);
	}

	[Fact]
	public async Task BatchAndContinuationOverloadsConvertDurationAndInstant()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var start = Instant.FromUtc(2026, 7, 20, 10, 0);
		await using var harness = new JobTestHarness(start.ToDateTimeOffset());
		var scheduler = new BatchWorkflowJob.Scheduler(
			harness.Storage,
			harness.Services.GetRequiredService<IJobSerializer>(),
			harness.TimeProvider,
			harness.Services.GetRequiredService<IIdGenerator>()
		);
		await using var batch = harness.Batches.Begin();

		var delayedBatchMember = scheduler.AddToBatch(
			batch,
			new("delayed-batch-member"),
			Duration.FromMinutes(5)
		);
		var absoluteBatchMember = scheduler.AddToBatchAt(
			batch,
			new("absolute-batch-member"),
			start + Duration.FromHours(2)
		);
		var firstParent = scheduler.AddToBatch(batch, new("first-parent"));
		var secondParent = scheduler.AddToBatch(batch, new("second-parent"));
		var jobContinuation = await scheduler.ScheduleAfterAsync(
			firstParent,
			new("job-continuation"),
			delay: Duration.FromMinutes(10),
			cancellationToken: cancellationToken
		);
		var fanInContinuation = await scheduler.ScheduleAfterAsync(
			[firstParent, secondParent],
			new("fan-in-continuation"),
			delay: Duration.FromMinutes(15),
			cancellationToken: cancellationToken
		);
		var batchHandle = await batch.CommitAsync(cancellationToken);
		var batchContinuation = await scheduler.ScheduleAfterAsync(
			batchHandle,
			new("batch-continuation"),
			delay: Duration.FromMinutes(20),
			cancellationToken: cancellationToken
		);

		var jobs = (await harness.QueryJobsAsync(cancellationToken: cancellationToken))
			.ToDictionary(static job => job.Id, StringComparer.Ordinal);
		Assert.Equal(start + Duration.FromMinutes(5), Instant.FromDateTimeOffset(jobs[delayedBatchMember.Id].DueAt));
		Assert.Equal(start + Duration.FromHours(2), Instant.FromDateTimeOffset(jobs[absoluteBatchMember.Id].DueAt));
		Assert.Equal(start + Duration.FromMinutes(10), Instant.FromDateTimeOffset(jobs[jobContinuation.Id].DueAt));
		Assert.Equal(start + Duration.FromMinutes(15), Instant.FromDateTimeOffset(jobs[fanInContinuation.Id].DueAt));
		Assert.Equal(start + Duration.FromMinutes(20), Instant.FromDateTimeOffset(jobs[batchContinuation.Id].DueAt));
	}

	[Fact]
	public async Task RecurringOverloadUsesNodaTimeZoneId()
	{
		var scheduler = new CaptureOnlyRecurringJobScheduler();
		var zone = DateTimeZoneProviders.Tzdb["Europe/Vienna"];

		await scheduler.AddOrUpdateRecurringAsync(
			"daily-report",
			"0 8 * * *",
			zone,
			TestContext.Current.CancellationToken
		);

		var capture = Assert.Single(scheduler.Captures);
		Assert.Equal(RecurringJobOperation.AddOrUpdate, capture.Operation);
		Assert.Equal(zone.Id, capture.TimeZone);
	}

	[Fact]
	public void SerializerRoundTripsNodaTimePayload()
	{
		var serializer = new NodaTimeJobSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web));
		var expected = new NodaPayload(
			Instant.FromUtc(2026, 7, 20, 10, 0),
			Duration.FromMilliseconds(1250),
			DateTimeZoneProviders.Tzdb["Europe/Vienna"]
		);

		var json = serializer.Serialize(expected, static options => new NodaPayloadJsonContext(options).NodaPayload);
		var actual = serializer.Deserialize(json, static options => new NodaPayloadJsonContext(options).NodaPayload);

		Assert.Equal(expected.Instant, actual.Instant);
		Assert.Equal(expected.Duration, actual.Duration);
		Assert.Equal(expected.Zone.Id, actual.Zone.Id);
	}

	public sealed record NodaPayload(Instant Instant, Duration Duration, DateTimeZone Zone);

	public sealed record SchedulerRequest(string Value);
}

[JsonSerializable(typeof(NodaTimeTests.NodaPayload))]
internal sealed partial class NodaPayloadJsonContext : JsonSerializerContext;
#pragma warning restore CS1591

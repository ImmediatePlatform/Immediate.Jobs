using System.Text.Json;
using System.Text.Json.Serialization;
using Immediate.Handlers.Shared;
using Immediate.Jobs.NodaTime;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Immediate.Jobs.FunctionalTests.Packages;

public sealed class NodaTimeTests
{
	[Fact]
	public async Task SchedulerOverloadsConvertDurationAndInstant()
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

		_ = await scheduler.ScheduleAsync(
			new("later"),
			Duration.FromMinutes(5),
			groupId: "tenant-a",
			cancellationToken: cancellationToken
		);
		_ = await scheduler.ScheduleAsync(
			new("absolute"),
			start + Duration.FromHours(2),
			groupId: "tenant-b",
			cancellationToken: cancellationToken
		);

		Assert.Equal(start + Duration.FromMinutes(5), Instant.FromDateTimeOffset(harness.Captures.Jobs[0].DueAt));
		Assert.Equal(start + Duration.FromHours(2), Instant.FromDateTimeOffset(harness.Captures.Jobs[1].DueAt));
		Assert.Equal("tenant-a", harness.Captures.Jobs[0].GroupId);
		Assert.Equal("tenant-b", harness.Captures.Jobs[1].GroupId);
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

		var delayedBatchMember = scheduler.Schedule(
			new("delayed-batch-member"),
			batch,
			Duration.FromMinutes(5)
		);
		var absoluteBatchMember = scheduler.Schedule(
			new("absolute-batch-member"),
			batch,
			start + Duration.FromHours(2)
		);
		var firstParent = scheduler.Enqueue(new("first-parent"), batch);
		var secondParent = scheduler.Enqueue(new("second-parent"), batch);
		var jobContinuation = scheduler.ScheduleAfter(
			new("job-continuation"),
			firstParent,
			delay: Duration.FromMinutes(10)
		);
		var fanInContinuation = scheduler.ScheduleAfter(
			new("fan-in-continuation"),
			[firstParent, secondParent],
			delay: Duration.FromMinutes(15)
		);
		var batchHandle = await batch.CommitAsync(cancellationToken);
		var batchContinuation = await scheduler.ScheduleAfterAsync(
			new("batch-continuation"),
			batchHandle,
			delay: Duration.FromMinutes(20),
			cancellationToken: cancellationToken
		);

		var jobs = (await harness.QueryJobsAsync(cancellationToken: cancellationToken))
			.ToDictionary(static job => job.JobId);
		Assert.Equal(start + Duration.FromMinutes(5), Instant.FromDateTimeOffset(jobs[delayedBatchMember.JobId].DueAt));
		Assert.Equal(start + Duration.FromHours(2), Instant.FromDateTimeOffset(jobs[absoluteBatchMember.JobId].DueAt));
		Assert.Equal(start + Duration.FromMinutes(10), Instant.FromDateTimeOffset(jobs[jobContinuation.JobId].DueAt));
		Assert.Equal(start + Duration.FromMinutes(15), Instant.FromDateTimeOffset(jobs[fanInContinuation.JobId].DueAt));
		Assert.Equal(start + Duration.FromMinutes(20), Instant.FromDateTimeOffset(jobs[batchContinuation].DueAt));
	}

	[Fact]
	public async Task RecurringOverloadUsesNodaTimeZoneId()
	{
		await using var harness = new JobTestHarness();
		var scheduler = new NodaRecurringJob.Scheduler(
			harness.Storage,
			harness.Services.GetRequiredService<IJobSerializer>(),
			harness.TimeProvider,
			harness.Services.GetRequiredService<IIdGenerator>()
		);
		var zone = DateTimeZoneProviders.Tzdb["Europe/Vienna"];

		await scheduler.AddOrUpdateRecurringAsync(
			"daily-report",
			"0 8 * * *",
			zone,
			TestContext.Current.CancellationToken
		);

		var capture = Assert.Single(harness.Captures.RecurringSchedules);
		Assert.Equal("daily-report", capture.Name);
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

[Handler, Job(Name = "noda-recurring")]
public static partial class NodaRecurringJob
{
	private static ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken)
	{
		_ = request;
		_ = cancellationToken;
		return ValueTask.CompletedTask;
	}
}

[JsonSerializable(typeof(NodaTimeTests.NodaPayload))]
internal sealed partial class NodaPayloadJsonContext : JsonSerializerContext;

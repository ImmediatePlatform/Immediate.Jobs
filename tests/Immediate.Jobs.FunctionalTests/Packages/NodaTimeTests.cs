using System.Text.Json;
using System.Text.Json.Serialization;
using Immediate.Jobs.NodaTime;
using Immediate.Jobs.Testing;
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

		_ = await scheduler.Schedule(new("later"), Duration.FromMinutes(5), cancellationToken);
		_ = await scheduler.ScheduleAt(new("absolute"), start + Duration.FromHours(2), cancellationToken);

		Assert.Equal(start + Duration.FromMinutes(5), Instant.FromDateTimeOffset(scheduler.Captures[0].RunAt));
		Assert.Equal(start + Duration.FromHours(2), Instant.FromDateTimeOffset(scheduler.Captures[1].RunAt));
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

	public sealed record SchedulerRequest(string Value) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}
}

[JsonSerializable(typeof(NodaTimeTests.NodaPayload))]
internal sealed partial class NodaPayloadJsonContext : JsonSerializerContext;
#pragma warning restore CS1591

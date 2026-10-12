namespace Immediate.Jobs.Tests;

public sealed class TimeSpanSerializerTests
{
	[Theory]
	[InlineData(0L, "PT0S")]
	[InlineData(1L, "PT0.0000001S")]
	[InlineData(-1L, "-PT0.0000001S")]
	[InlineData(900610000001L, "P1DT1H1M1.0000001S")]
	[InlineData(long.MaxValue, "P10675199DT2H48M5.4775807S")]
	[InlineData(long.MinValue, "-P10675199DT2H48M5.4775808S")]
	public void DurationsRoundTripWithoutLosingTicks(long ticks, string serialized)
	{
		var duration = TimeSpan.FromTicks(ticks);
		Assert.Equal(serialized, TimeSpanSerializer.Serialize(duration));
		Assert.Equal(duration, TimeSpanSerializer.Deserialize(serialized));
		Assert.True(TimeSpanSerializer.TryParse(serialized, out var parsed));
		Assert.Equal(duration, parsed);
	}

	[Theory]
	[InlineData("00:00:01.0000001")]
	[InlineData("PT1.0000001S")]
	public void AcceptsBothDurationFormats(string value)
	{
		Assert.True(TimeSpanSerializer.TryParse(value, out var parsed));
		Assert.Equal(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1), parsed);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("PT")]
	[InlineData("not a duration")]
	[InlineData("P10675200D")]
	public void InvalidAndOverflowingDurationsAreRejected(string? value) =>
		Assert.False(TimeSpanSerializer.TryParse(value, out _));
}

using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Immediate.Jobs.DistributedAspire.Shared.Data;

[Owned]
public sealed class PostgresDateTimeOffset
{
	public static PostgresDateTimeOffset New(DateTimeOffset dateTimeOffset)
		=> new()
		{
			UtcDateTime = dateTimeOffset.ToUniversalTime(),
			LocalDateTime = new DateTime(dateTimeOffset.LocalDateTime.Ticks, DateTimeKind.Utc),
			TimeZoneOffset = dateTimeOffset.Offset,
			TimeZoneName = DateTimeZoneProviders.Tzdb.GetSystemDefault().Id,
		};

	public DateTimeOffset UtcDateTime { get; init; }

	public DateTime LocalDateTime { get; init; }

	public TimeSpan TimeZoneOffset { get; init; }

	public string TimeZoneName { get; init; } = default!;

	public DateTimeOffset GetLocalDateTimeOffset()
		=> new(LocalDateTime.Ticks, TimeZoneOffset);

	public override string ToString()
	   => $"{UtcDateTime:O} ({LocalDateTime:O} {TimeZoneOffset} {TimeZoneName})";
}

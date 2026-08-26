using Cronos;

namespace Immediate.Jobs.Shared.Internals;

internal static class JobCron
{
	public static CronExpression Parse(string cron)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cron);

		Span<Range> splits = stackalloc Range[7];
		var numSplits = cron.AsSpan().Trim().Split(splits, ' ');

		var format = numSplits switch
		{
			6 => CronFormat.IncludeSeconds,
			_ => CronFormat.Standard,
		};

		return CronExpression.Parse(cron, format);
	}

	public static TimeZoneInfo GetTimeZone(string timeZone)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);

		if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var tzi))
			throw new ArgumentException($"Unknown time-zone identifier '{timeZone}'.", nameof(timeZone));

		return tzi;
	}
}

using System.Globalization;

namespace Immediate.Jobs;

/// <summary>
///	    Validates a cron expression against the syntax the runtime accepts. Jobs are parsed at
///	    runtime by Cronos, so anything Cronos accepts has to be accepted here: this reports an
///	    error, and a false positive fails a build over a working expression.
/// </summary>
internal static class CronValidator
{
	private enum FieldKind
	{
		Second,
		Minute,
		Hour,
		DayOfMonth,
		Month,
		DayOfWeek,
	}

	private static readonly string[] Macros =
	[
		"@yearly", "@annually", "@monthly", "@weekly", "@daily", "@midnight", "@hourly",
		"@every_second", "@every_minute",
	];

	private static readonly string[] Months =
		["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

	private static readonly string[] DaysOfWeek =
		["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

	private static readonly FieldKind[] SixFields =
		[FieldKind.Second, FieldKind.Minute, FieldKind.Hour, FieldKind.DayOfMonth, FieldKind.Month, FieldKind.DayOfWeek];

	private static readonly FieldKind[] FiveFields =
		[FieldKind.Minute, FieldKind.Hour, FieldKind.DayOfMonth, FieldKind.Month, FieldKind.DayOfWeek];

	public static bool TryValidate(string cron, out string error)
	{
		if (string.IsNullOrWhiteSpace(cron))
		{
			error = "the expression is empty";
			return false;
		}

		var trimmed = cron.Trim();
		if (trimmed[0] is '@')
		{
			if (Array.FindIndex(Macros, m => string.Equals(m, trimmed, StringComparison.OrdinalIgnoreCase)) >= 0)
			{
				error = string.Empty;
				return true;
			}

			error = $"'{trimmed}' is not a recognized macro";
			return false;
		}

		var fields = cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (fields.Length is not (5 or 6))
		{
			error = "expected five or six fields";
			return false;
		}

		var kinds = fields.Length == 6 ? SixFields : FiveFields;
		for (var index = 0; index < fields.Length; index++)
		{
			if (!ValidateField(fields[index], kinds[index], out var fieldError))
			{
				error = $"field {index + 1} ({kinds[index]}) is invalid: {fieldError}";
				return false;
			}
		}

		error = string.Empty;
		return true;
	}

	private static (int Minimum, int Maximum) GetRange(FieldKind kind) =>
		kind switch
		{
			FieldKind.Second or FieldKind.Minute => (0, 59),
			FieldKind.Hour => (0, 23),
			FieldKind.DayOfMonth => (1, 31),
			FieldKind.Month => (1, 12),
			// both 0 and 7 mean Sunday
			FieldKind.DayOfWeek or _ => (0, 7),
		};

	private static bool ValidateField(string field, FieldKind kind, out string error)
	{
		foreach (var item in field.Split(','))
		{
			if (item.Length == 0)
			{
				error = "empty list entry";
				return false;
			}

			var pieces = item.Split('/');
			if (pieces.Length > 2)
			{
				error = $"'{item}' has more than one step";
				return false;
			}

			if (pieces.Length == 2
				&& (!int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var step) || step <= 0))
			{
				error = $"'{item}' has a step that is not a positive number";
				return false;
			}

			if (!ValidateValue(pieces[0], kind, out error))
				return false;
		}

		error = string.Empty;
		return true;
	}

	private static bool ValidateValue(string value, FieldKind kind, out string error)
	{
		error = string.Empty;

		if (value is "*")
			return true;

		if (value is "?")
		{
			if (kind is FieldKind.DayOfMonth or FieldKind.DayOfWeek)
				return true;

			error = "'?' is only valid for the day-of-month and day-of-week fields";
			return false;
		}

		if (kind is FieldKind.DayOfMonth && TryValidateDayOfMonthSpecial(value, ref error) is { } dayOfMonthResult)
			return dayOfMonthResult;

		if (kind is FieldKind.DayOfWeek && TryValidateDayOfWeekSpecial(value, ref error) is { } dayOfWeekResult)
			return dayOfWeekResult;

		// a range's endpoints may wrap, e.g. `FRI-MON`, so only the endpoints themselves are checked
		foreach (var bound in value.Split('-'))
		{
			if (!IsValue(bound, kind))
			{
				error = $"'{value}' is not a valid value for this field";
				return false;
			}
		}

		if (value.Split('-').Length > 2)
		{
			error = $"'{value}' is not a valid range";
			return false;
		}

		return true;
	}

	/// <returns><see langword="null" /> when <paramref name="value" /> is not a special form.</returns>
	private static bool? TryValidateDayOfMonthSpecial(string value, ref string error)
	{
		if (value.Equals("L", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("LW", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		// `L-3`: three days before the last day of the month; `L-3W`: adjusted to the nearest weekday
		if (value.StartsWith("L-", StringComparison.OrdinalIgnoreCase))
		{
			var offset = value.Substring(2);
			if (offset.Length > 0 && offset[^1] is 'W' or 'w')
				offset = offset.Substring(0, offset.Length - 1);

			if (IsNumber(offset, minimum: 0, maximum: 30))
				return true;

			error = $"'{value}' must offset the last day of the month by 0 to 30 days";
			return false;
		}

		// `15W`: the weekday nearest the fifteenth
		if (value[^1] is 'W' or 'w')
		{
			if (IsNumber(value.Substring(0, value.Length - 1), 1, 31))
				return true;

			error = $"'{value}' must apply `W` to a day from 1 to 31";
			return false;
		}

		return null;
	}

	/// <returns><see langword="null" /> when <paramref name="value" /> is not a special form.</returns>
	private static bool? TryValidateDayOfWeekSpecial(string value, ref string error)
	{
		// `FRI#3`: the third Friday of the month
		if (value.Split('#') is { Length: > 1 } occurrence)
		{
			if (occurrence is [var day, var nth]
				&& IsValue(day, FieldKind.DayOfWeek)
				&& IsNumber(nth, minimum: 1, maximum: 5))
			{
				return true;
			}

			error = $"'{value}' must select occurrence 1 to 5 of a day of the week";
			return false;
		}

		// `5L`: the last Friday of the month
		if (value.Length > 1 && value[^1] is 'L' or 'l')
		{
			if (IsValue(value.Substring(0, value.Length - 1), FieldKind.DayOfWeek))
				return true;

			error = $"'{value}' must apply `L` to a day of the week";
			return false;
		}

		return value.Equals("L", StringComparison.OrdinalIgnoreCase) ? true : null;
	}

	private static bool IsValue(string value, FieldKind kind)
	{
		var (minimum, maximum) = GetRange(kind);
		if (IsNumber(value, minimum, maximum))
			return true;

		var names = kind switch
		{
			FieldKind.Month => Months,
			FieldKind.DayOfWeek => DaysOfWeek,
			FieldKind.Second or FieldKind.Minute or FieldKind.Hour or FieldKind.DayOfMonth or _ => null,
		};

		return names is not null
			&& Array.FindIndex(names, name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) >= 0;
	}

	private static bool IsNumber(string value, int minimum, int maximum) =>
		int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
		&& parsed >= minimum
		&& parsed <= maximum;
}

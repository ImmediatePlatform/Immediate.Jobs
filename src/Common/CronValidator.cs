using System.Globalization;

namespace Immediate.Jobs;

internal static class CronValidator
{
	public static bool TryValidate(string cron, out string error)
	{
		if (string.IsNullOrWhiteSpace(cron))
		{
			error = "the expression is empty";
			return false;
		}

		var fields = cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (fields.Length is not (5 or 6))
		{
			error = "expected five or six fields";
			return false;
		}

		(int Minimum, int Maximum)[] ranges = fields.Length == 6
			? [(0, 59), (0, 59), (0, 23), (1, 31), (1, 12), (0, 7)]
			: [(0, 59), (0, 23), (1, 31), (1, 12), (0, 7)];
		for (var index = 0; index < fields.Length; index++)
		{
			if (ValidateField(fields[index], ranges[index].Item1, ranges[index].Item2))
				continue;

			error = $"field {index + 1} is malformed or out of range";
			return false;
		}

		error = string.Empty;
		return true;
	}

	private static bool ValidateField(string field, int minimum, int maximum)
	{
		foreach (var item in field.Split(','))
		{
			var pieces = item.Split('/');
			if (pieces.Length > 2 || pieces.Length == 2 && (!int.TryParse(pieces[1], out var step) || step <= 0))
				return false;

			var range = pieces[0];
			if (range == "*")
				continue;

			var bounds = range.Split('-');
			if (bounds.Length > 2 || !TryValue(bounds[0], minimum, maximum, out var first))
				return false;
			if (bounds.Length == 2 && (!TryValue(bounds[1], minimum, maximum, out var last) || last < first))
				return false;
		}

		return true;
	}

	private static bool TryValue(string value, int minimum, int maximum, out int parsed) =>
		int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
		parsed >= minimum &&
		parsed <= maximum;
}

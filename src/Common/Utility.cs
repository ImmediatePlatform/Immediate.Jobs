using System.Globalization;
using System.Text;

namespace Immediate.Jobs;

internal static class Utility
{
	extension(string str)
	{
		public string AsJobName() => str.RemoveJobSuffix().ToKebabCase();

		public ReadOnlySpan<char> RemoveJobSuffix() =>
			str.EndsWith("Job", StringComparison.OrdinalIgnoreCase)
				? str.AsSpan()[..^3]
				: str;

		public string AsQueueName() => str.AsSpan().ToKebabCase();

		public bool HasNameContent()
		{
			foreach (var character in str)
			{
				if (char.IsLetterOrDigit(character))
					return true;
			}

			return false;
		}
	}

	extension(ReadOnlySpan<char> str)
	{
		public string ToKebabCase()
		{
			if (str.IsEmpty)
				return string.Empty;

			var result = new StringBuilder(str.Length + 8);
			_ = result.Append(char.ToLower(str[0], CultureInfo.InvariantCulture));

			for (var index = 1; index < str.Length; index++)
			{
				var current = str[index];

				if (
					char.IsUpper(current)
					&& (char.IsLower(str[index - 1])
						|| (index + 1 < str.Length
							&& char.IsLower(str[index + 1])
						)
					)
				)
				{
					_ = result.Append('-');
				}

				_ = result.Append(char.ToLower(current, CultureInfo.InvariantCulture));
			}

			return result.ToString();
		}
	}
}

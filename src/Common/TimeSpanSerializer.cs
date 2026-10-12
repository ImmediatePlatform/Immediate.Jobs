using System.Globalization;
using System.Xml;

namespace Immediate.Jobs;

internal static class TimeSpanSerializer
{
	public static string Serialize(TimeSpan value) => XmlConvert.ToString(value);

	public static TimeSpan Deserialize(string value) => XmlConvert.ToTimeSpan(value);

	public static bool TryParse(string? value, out TimeSpan result)
	{
		if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out result))
			return true;

		if (value is null)
			return false;

		try
		{
			result = Deserialize(value);
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
		catch (OverflowException)
		{
			return false;
		}
	}
}

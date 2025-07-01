namespace Immediate.Jobs.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public sealed class RecurringJobAttribute : Attribute
{
	public RecurringJobAttribute(
		string cronExpression,
		string timeZone
	)
	{
		CronExpression = cronExpression;
		TimeZone = timeZone;
	}

	public string CronExpression { get; }
	public string TimeZone { get; }
}

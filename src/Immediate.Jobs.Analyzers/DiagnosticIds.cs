namespace Immediate.Jobs.Analyzers;

internal static class DiagnosticIds
{
	public const string IJOB0001MissingHandlerAttribute = "IJOB0001";
	public const string IJOB0002DuplicateJobName = "IJOB0002";
	public const string IJOB0003DuplicateQueueName = "IJOB0003";
	public const string IJOB0004NodaTimePackageRequired = "IJOB0004";
	public const string IJOB0005JobConfigurationInvalid = "IJOB0005";
	public const string IJOB0006CronJobCannotHaveParameters = "IJOB0006";
	public const string IJOB0007CronJobConfigurationInvalid = "IJOB0007";
	public const string IJOB0008JobNameInvalid = "IJOB0008";
	public const string IJOB0020DetachedJobCannotBeAddedToBatch = "IJOB0020";
}

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.StorageTests;
using Immediate.Jobs.Testing;
using Xunit.Sdk;

[assembly: RegisterXunitSerializer(typeof(JobStorageConformanceTestCaseSerializer), typeof(JobStorageConformanceTestCase))]

namespace Immediate.Jobs.StorageTests;

[SuppressMessage("Performance", "CA1812", Justification = "Used via attribute")]
internal sealed class JobStorageConformanceTestCaseSerializer : IXunitSerializer
{
	public object Deserialize(Type type, string serializedValue) =>
		JobStorageConformanceSuite.AllCasesByName[serializedValue];

	public bool IsSerializable(Type type, object? value, [NotNullWhen(false)] out string? failureReason)
	{
		if (type == typeof(JobStorageConformanceTestCase))
		{
			failureReason = null;
			return true;
		}

		failureReason = "Unknown type.";
		return false;
	}

	public string Serialize(object value) =>
		value is JobStorageConformanceTestCase { Name: { } name }
		? name
		: throw new UnreachableException();
}

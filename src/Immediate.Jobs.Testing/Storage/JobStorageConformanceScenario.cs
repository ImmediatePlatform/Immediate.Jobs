using Immediate.Jobs.Shared.Storage;

#pragma warning disable IDE0130
namespace Immediate.Jobs.Testing;

internal delegate ValueTask JobStorageConformanceScenario(
	IJobStorage storage,
	IServiceProvider serviceProvider,
	CancellationToken cancellationToken
);

internal sealed class JobStorageConformanceCaseDefinition(
	string name,
	StorageCapabilities requiredCapabilities,
	JobStorageConformanceScenario scenario
)
{
	internal string Name { get; } = name;

	internal StorageCapabilities RequiredCapabilities { get; } = requiredCapabilities;

	internal JobStorageConformanceScenario Scenario { get; } = scenario;
}

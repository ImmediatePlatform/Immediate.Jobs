using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal delegate ValueTask JobStorageConformanceScenario(
	IJobStorage storage,
	FakeTimeProvider timeProvider,
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

using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.Testing.Storage;

internal delegate ValueTask JobStorageConformanceScenario(
	IJobStorage storage,
	FakeTimeProvider timeProvider,
	CancellationToken cancellationToken
);

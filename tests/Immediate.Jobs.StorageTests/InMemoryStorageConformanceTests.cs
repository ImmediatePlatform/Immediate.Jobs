using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryStorageConformanceTests
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring |
		StorageCapabilities.Graph |
		StorageCapabilities.FairQueues;

	public static TheoryData<JobStorageConformanceTestCase> Cases =>
		[.. JobStorageConformanceSuite.GetCases(Capabilities)];

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task InMemoryStorageConforms(JobStorageConformanceTestCase testCase)
	{
		ArgumentNullException.ThrowIfNull(testCase);
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var services = new ServiceCollection();
		_ = services.AddSingleton<TimeProvider>(clock);
		_ = services.AddSingleton(clock);
		_ = services.AddImmediateJobsCore().ConfigureStorage(options => _ = options.UseInMemory());
		await using var provider = services.BuildServiceProvider(validateScopes: true);

		await testCase.RunAsync(provider, TestContext.Current.CancellationToken);
	}
}

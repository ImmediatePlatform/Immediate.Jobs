using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;
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
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<TimeProvider>(clock);
		services.AddSingleton(clock);
		services.AddImmediateJobsCore().ConfigureStorage(options => _ = options.UseInMemory());

		await using var provider = services.BuildServiceProvider(
			new ServiceProviderOptions
			{
				ValidateOnBuild = true,
				ValidateScopes = true,
			}
		);

		var storage = (InMemoryJobStorage)provider.GetRequiredService<IJobStorage>();

		storage.LoadPersistedJobState(
			testCase.PersistedJobState.Jobs,
			testCase.PersistedJobState.Batches,
			testCase.PersistedJobState.Edges,
			testCase.PersistedJobState.RecurringSchedules
		);

		await testCase.RunAsync(provider, TestContext.Current.CancellationToken);
	}
}

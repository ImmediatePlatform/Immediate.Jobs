using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Immediate.Jobs.Testing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class CapturingStorageConformanceTests
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
	public async Task CapturingStorageConforms(JobStorageConformanceTestCase testCase)
	{
		ArgumentNullException.ThrowIfNull(testCase);
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var services = new ServiceCollection();
		_ = services.AddSingleton<TimeProvider>(clock);
		_ = services.AddSingleton(clock);
		_ = services.AddSingleton<CapturingJobStorage>();
		_ = services.AddImmediateJobsCore().ConfigureStorage(options => options
			.UseStorage(static provider => provider.GetRequiredService<CapturingJobStorage>())
			.UseDistributed());
		await using var provider = services.BuildServiceProvider(validateScopes: true);

		await testCase.RunAsync(provider, TestContext.Current.CancellationToken);
	}
}

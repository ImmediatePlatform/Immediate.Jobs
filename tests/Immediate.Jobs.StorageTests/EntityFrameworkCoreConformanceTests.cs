using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;

namespace Immediate.Jobs.StorageTests;

[Collection(StorageContainerFixtureGroup.Name)]
public sealed class EntityFrameworkCoreConformanceTests(StorageContainers containers)
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring |
		StorageCapabilities.Graph |
		StorageCapabilities.FairQueues |
		StorageCapabilities.Replica;

	public static TheoryData<ConformanceDatabase, ConformanceTopology, JobStorageConformanceTestCase> Cases =>
		CreateCases();

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task EntityFrameworkCoreConforms(
		ConformanceDatabase database,
		ConformanceTopology topology,
		JobStorageConformanceTestCase testCase
	)
	{
		ArgumentNullException.ThrowIfNull(testCase);
		await using var fixture = await RelationalConformanceFixture.CreateAsync(
			containers,
			database,
			ConformanceAdapter.EntityFrameworkCore,
			TestContext.Current.CancellationToken,
			useDistributedTopology: topology == ConformanceTopology.Distributed
		);
		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}

	private static TheoryData<ConformanceDatabase, ConformanceTopology, JobStorageConformanceTestCase> CreateCases()
	{
		var data = new TheoryData<ConformanceDatabase, ConformanceTopology, JobStorageConformanceTestCase>();
		foreach (var database in Enum.GetValues<ConformanceDatabase>())
		{
			foreach (var topology in Enum.GetValues<ConformanceTopology>())
			{
				var capabilities = topology == ConformanceTopology.Distributed
					? Capabilities
					: Capabilities & ~StorageCapabilities.Replica;
				foreach (var testCase in JobStorageConformanceSuite.GetCases(capabilities))
					data.Add(database, topology, testCase);
			}
		}

		return data;
	}
}

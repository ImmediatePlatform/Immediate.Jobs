using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;

namespace Immediate.Jobs.StorageTests;

[Collection(RedisContainerFixtureGroup.Name)]
public sealed class RedisConformanceTests(RedisStorageFixture redis)
{
	private const StorageCapabilities Capabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring;

	public static TheoryData<JobStorageConformanceTestCase> Cases =>
		[.. JobStorageConformanceSuite.GetCases(Capabilities)];

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task RedisConforms(JobStorageConformanceTestCase testCase)
	{
		ArgumentNullException.ThrowIfNull(testCase);
		await using var fixture = await RedisConformanceFixture.CreateAsync(
			redis.Container.GetConnectionString(),
			TestContext.Current.CancellationToken
		);
		await testCase.RunAsync(fixture.Services, TestContext.Current.CancellationToken);
	}
}

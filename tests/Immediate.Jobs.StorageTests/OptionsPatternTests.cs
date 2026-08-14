using Immediate.Jobs.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class OptionsPatternTests
{
	[Fact]
	public void RedisConfigurationUsesOptionsPattern()
	{
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var services = new ServiceCollection();
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddImmediateJobsCore().ConfigureStorage(storage =>
			storage.UseRedis("unused", options =>
			{
				options.Database = 4;
				options.KeyPrefix = "configured";
			})
		);

		using var provider = services.BuildServiceProvider();
		var options = provider.GetRequiredService<IOptions<RedisJobStorageOptions>>().Value;

		Assert.Equal(4, options.Database);
		Assert.Equal("configured", options.KeyPrefix);
	}

	[Fact]
	public void RedisOptionsRejectInvalidKeyPrefixes()
	{
		var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		var services = new ServiceCollection();
		_ = services.AddSingleton<TimeProvider>(timeProvider);
		_ = services.AddImmediateJobsCore().ConfigureStorage(storage =>
			storage.UseRedis("unused", options => options.KeyPrefix = "{invalid}")
		);

		using var provider = services.BuildServiceProvider();

		_ = Assert.Throws<OptionsValidationException>(
			() => provider.GetRequiredService<IOptions<RedisJobStorageOptions>>().Value
		);
	}
}

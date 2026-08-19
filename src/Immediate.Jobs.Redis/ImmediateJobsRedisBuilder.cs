using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Redis;

/// <summary>
/// 	The fluent registration result returned by <see cref="RedisServiceCollectionExtensions.UseRedis(IImmediateJobsStorageBuilder)"/>.
/// </summary>
public interface IImmediateJobsRedisBuilder : IImmediateJobsStorageBuilder
{
	/// <summary>
	///		Provides an extension point to configure the options using a user provided configuration method.
	/// </summary>
	/// <param name="configureRedis">
	///		The configuration method used to set the options.
	///	</param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsRedisBuilder ConfigureRedis(
		Action<OptionsBuilder<RedisJobStorageOptions>> configureRedis
	);
	/// <summary>
	///		Provides an extension point to configure the options using a user provided configuration method.
	/// </summary>
	/// <param name="configureRedis">
	///		The configuration method used to set the options.
	///	</param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsRedisBuilder ConfigureRedis(
		Action<RedisJobStorageOptions> configureRedis
	);
}

internal sealed class ImmediateJobsRedisBuilder(IImmediateJobsStorageBuilder builder, OptionsBuilder<RedisJobStorageOptions> optionsBuilder) : IImmediateJobsRedisBuilder
{
	public IImmediateJobsRedisBuilder ConfigureRedis(Action<OptionsBuilder<RedisJobStorageOptions>> configureRedis)
	{
		ArgumentNullException.ThrowIfNull(configureRedis);

		configureRedis(optionsBuilder);
		return this;
	}

	public IImmediateJobsRedisBuilder ConfigureRedis(Action<RedisJobStorageOptions> configureRedis)
	{
		ArgumentNullException.ThrowIfNull(configureRedis);

		optionsBuilder.Configure(configureRedis);
		return this;
	}

	public IServiceCollection Services => builder.Services;

	public IImmediateJobsStorageBuilder UseDistributed() =>
		builder.UseDistributed();

	public IImmediateJobsStorageBuilder UseDistributed(Func<IServiceProvider, IJobStorage> durableStorageFactory) =>
		builder.UseDistributed(durableStorageFactory);

	public IImmediateJobsStorageBuilder UseInMemory() =>
		builder.UseInMemory();

	public IImmediateJobsStorageBuilder UseSingleServer() =>
		builder.UseSingleServer();

	public IImmediateJobsStorageBuilder UseSingleServer(Func<IServiceProvider, IJobStorage> durableStorageFactory) =>
		builder.UseSingleServer(durableStorageFactory);

	public IImmediateJobsStorageBuilder UseStorage(Func<IServiceProvider, IJobStorage> factory) =>
		builder.UseStorage(factory);

	IImmediateJobsStorageBuilder IImmediateJobsStorageBuilder.UseStorage<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJobStorage
	>() =>
		builder.UseStorage<TJobStorage>();
}

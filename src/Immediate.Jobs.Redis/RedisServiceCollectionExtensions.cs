using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

/// <summary>Registers Redis job storage.</summary>
public static class RedisServiceCollectionExtensions
{
	/// <summary>
	///		Selects Redis as the distributed Immediate.Jobs queue and recurring provider.
	/// </summary>
	/// <remarks>
	///		Redis does not implement graph storage, so batches and continuations require a SQL provider.
	///		This method always selects distributed mode; single-server mode requires a full-capability
	///		durable replica and is not supported by the Redis provider.
	/// </remarks>
	/// <param name="builder">
	///		The Immediate.Jobs storage options builder to configure.
	/// </param>
	/// <param name="configuration">
	///		The Redis configuration string.
	/// </param>
	/// <param name="configure">
	///		An optional callback that configures Redis key placement.
	/// </param>
	/// <returns>
	///		The configured Immediate.Jobs options.
	/// </returns>
	public static ImmediateJobsStorageBuilder UseRedis(
		this ImmediateJobsStorageBuilder builder,
		string configuration,
		Action<RedisJobStorageOptions>? configure = null
	)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
		ConfigureOptions(builder, configure);
		return builder
			.UseStorage(services => new RedisJobStorage(
				ConnectionMultiplexer.Connect(configuration),
				services.GetRequiredService<IOptions<RedisJobStorageOptions>>(),
				services.GetService<TimeProvider>(),
				ownsConnection: true
			))
			.UseDistributed();
	}

	/// <summary>Selects an application-owned Redis connection as the distributed job provider.</summary>
	/// <remarks>
	/// Redis does not implement graph storage, so batches and continuations require a SQL provider.
	/// The supplied connection is not disposed by the job provider.
	/// </remarks>
	/// <param name="jobs">The Immediate.Jobs options to configure.</param>
	/// <param name="connection">The application-owned Redis connection.</param>
	/// <param name="configure">An optional callback that configures Redis key placement.</param>
	/// <returns>The configured Immediate.Jobs options.</returns>
	public static ImmediateJobsStorageBuilder UseRedis(
		this ImmediateJobsStorageBuilder jobs,
		IConnectionMultiplexer connection,
		Action<RedisJobStorageOptions>? configure = null
	)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(connection);
		ConfigureOptions(jobs, configure);
		return jobs
			.UseStorage(services => new RedisJobStorage(
				connection,
				services.GetRequiredService<IOptions<RedisJobStorageOptions>>(),
				services.GetService<TimeProvider>(),
				ownsConnection: false
			))
			.UseDistributed();
	}

	private static void ConfigureOptions(
		ImmediateJobsStorageBuilder builder,
		Action<RedisJobStorageOptions>? configure
	)
	{
		builder.ConfigureOptions<RedisJobStorageOptions>(optionsBuilder =>
		{
			if (configure is not null)
				optionsBuilder.Configure(configure);

			optionsBuilder
				.Validate(
					static options => !string.IsNullOrWhiteSpace(options.KeyPrefix),
					"The Redis key prefix cannot be empty."
				)
				.Validate(
					static options => options.KeyPrefix.IndexOfAny(['{', '}']) < 0,
					"The Redis key prefix cannot contain '{' or '}'."
				)
				.ValidateOnStart();
		});
	}
}

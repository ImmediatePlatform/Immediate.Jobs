using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

/// <summary>Registers Redis job storage.</summary>
public static class RedisServiceCollectionExtensions
{
	/// <summary>Selects Redis as the distributed Immediate.Jobs queue and recurring provider.</summary>
	/// <remarks>
	/// Redis does not implement graph storage, so batches and continuations require a SQL provider.
	/// This method always selects distributed mode; single-server mode requires a full-capability
	/// durable replica and is not supported by the Redis provider.
	/// </remarks>
	/// <param name="jobs">The Immediate.Jobs options to configure.</param>
	/// <param name="configuration">The Redis configuration string.</param>
	/// <param name="configure">An optional callback that configures Redis key placement.</param>
	/// <returns>The configured Immediate.Jobs options.</returns>
	public static ImmediateJobsOptions UseRedis(
		this ImmediateJobsOptions jobs,
		string configuration,
		Action<RedisJobStorageOptions>? configure = null
	)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
		var options = CreateOptions(configure);
		return jobs
			.UseStorage(services => new RedisJobStorage(
				ConnectionMultiplexer.Connect(configuration),
				options,
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
	public static ImmediateJobsOptions UseRedis(
		this ImmediateJobsOptions jobs,
		IConnectionMultiplexer connection,
		Action<RedisJobStorageOptions>? configure = null
	)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(connection);
		var options = CreateOptions(configure);
		return jobs
			.UseStorage(services => new RedisJobStorage(
				connection,
				options,
				services.GetService<TimeProvider>(),
				ownsConnection: false
			))
			.UseDistributed();
	}

	private static RedisJobStorageOptions CreateOptions(Action<RedisJobStorageOptions>? configure)
	{
		var options = new RedisJobStorageOptions();
		configure?.Invoke(options);
		ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);
		if (options.KeyPrefix.IndexOfAny(['{', '}']) >= 0)
			throw new ArgumentException("The Redis key prefix cannot contain '{' or '}'.", nameof(configure));
		return options;
	}
}

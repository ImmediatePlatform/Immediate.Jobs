using Immediate.Validations.Shared;
using Microsoft.Extensions.DependencyInjection;

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
	/// <returns>
	///		The configured Immediate.Jobs options.
	/// </returns>
	public static IImmediateJobsRedisBuilder UseRedis(
		this IImmediateJobsStorageBuilder builder
	)
	{
		ArgumentNullException.ThrowIfNull(builder);

		builder
			.UseStorage<RedisJobStorage>()
			.UseDistributed();

		var optionsBuilder = builder.Services
			.AddOptionsWithValidateOnStart<RedisJobStorageOptions>()
			.Validate(
				o =>
				{
					ValidationException.ThrowIfInvalid(o, $@"Validation error for ""{nameof(RedisJobStorageOptions)}""");
					return true;
				}
			);

		return new ImmediateJobsRedisBuilder(builder, optionsBuilder);
	}
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.EntityFrameworkCore;

/// <summary>Registers EF Core job storage.</summary>
public static class EntityFrameworkCoreServiceCollectionExtensions
{
	/// <summary>
	///		Selects EF Core as the Immediate.Jobs storage provider.
	/// </summary>
	/// <remarks>
	///		The context must be registered with AddDbContextFactory and call ModelBuilder.AddImmediateJobs.
	/// </remarks>
	/// <typeparam name="TContext">
	///		The application context containing the Immediate.Jobs model.
	/// </typeparam>
	/// <param name="builder">
	///		The Immediate.Jobs storage options builder to configure.
	/// </param>
	/// <returns>
	///		The configured Immediate.Jobs options.
	/// </returns>
	public static ImmediateJobsStorageBuilder UseEntityFrameworkCore<TContext>(this ImmediateJobsStorageBuilder builder)
		where TContext : DbContext
	{
		ArgumentNullException.ThrowIfNull(builder);

		return builder.UseStorage(
			services =>
				new EntityFrameworkCoreJobStorage<TContext>(
					services.GetRequiredService<IDbContextFactory<TContext>>(),
					services.GetService<TimeProvider>()
				)
		);
	}
}

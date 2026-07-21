using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.EntityFrameworkCore;

/// <summary>Registers EF Core job storage.</summary>
public static class EntityFrameworkCoreServiceCollectionExtensions
{
	/// <summary>Selects EF Core as the Immediate.Jobs storage provider.</summary>
	/// <remarks>The context must be registered with AddDbContextFactory and call ModelBuilder.AddImmediateJobs.</remarks>
	public static ImmediateJobsOptions UseEntityFrameworkCore<TContext>(this ImmediateJobsOptions jobs)
		where TContext : DbContext
	{
		ArgumentNullException.ThrowIfNull(jobs);
		return jobs.UseStorage(services => new EntityFrameworkCoreJobStorage<TContext>(
			services.GetRequiredService<IDbContextFactory<TContext>>(),
			services.GetService<TimeProvider>()
		));
	}
}

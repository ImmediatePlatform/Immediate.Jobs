using Immediate.Jobs.DistributedAspire.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Immediate.Jobs.DistributedAspire.Api;

public sealed class JobsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
	public JobsDbContext CreateDbContext(string[] args)
	{
		var optionsBuilder = new DbContextOptionsBuilder<JobsDbContext>()
			.UseNpgsql(npgsql => npgsql.MigrationsAssembly("Immediate.Jobs.DistributedAspire.Shared"));

		return new JobsDbContext(optionsBuilder.Options);
	}
}

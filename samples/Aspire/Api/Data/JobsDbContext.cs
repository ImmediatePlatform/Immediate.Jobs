using Immediate.Jobs.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Immediate.Jobs.Aspire.Api.Data;

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
{
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);
		_ = modelBuilder.AddImmediateJobs();
	}
}

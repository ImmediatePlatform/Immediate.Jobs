using Immediate.Jobs.DistributedAspire.Shared.Data.Contracts;
using Immediate.Jobs.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using static Immediate.Jobs.DistributedAspire.Shared.Data.BatchEntity;

namespace Immediate.Jobs.DistributedAspire.Shared.Data;

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
{
	public DbSet<BatchEntity> BatchableRows { get; set; } = default!;

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		_ = modelBuilder
			.AddImmediateJobs()
			.ApplyConfiguration(new BatchEntityConfiguration());
	}

	public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
	{
		var entries = ChangeTracker
			.Entries()
			.Where(e => e.State is EntityState.Modified or EntityState.Added)
			.ToList();

		if (entries.Count == 0)
		{
			return base.SaveChangesAsync(cancellationToken);
		}

		var now = DateTimeOffset.UtcNow;

		foreach (var entry in entries)
		{
			if (entry.Entity is IModified changed)
			{
				changed.ModifiedOn = now;
			}

			if (entry.Entity is IRowVersion rowVersion)
			{
				rowVersion.Version = Random.Shared.NextInt64();
			}
		}

		return base.SaveChangesAsync(cancellationToken);
	}
}

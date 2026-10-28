using Immediate.Jobs.Shared.Apis;
using Microsoft.EntityFrameworkCore;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	private async ValueTask InterruptActiveLeasesAsync(CancellationToken cancellationToken)
	{
		const int BatchSize = 1000;
		var now = _timeProvider.GetUtcNow();
		while (true)
		{
			var count = 0;
			await RetryConcurrencyAsync(async token =>
			{
				await using var context = await contextFactory.CreateDbContextAsync(token);
				await using var transaction = await context.Database.BeginTransactionAsync(token);
				var jobs = await context.Set<ImmediateJobEntity>()
					.Where(job => job.State == JobState.Active)
					.OrderBy(job => job.Id)
					.Take(BatchSize)
					.ToListAsync(token);
				count = jobs.Count;
				foreach (var job in jobs)
				{
					if (await GetOrMaterializeExecutionAsync(context, job, token) is { } execution)
					{
						execution.State = JobExecutionState.Interrupted;
						execution.CompletedAt = now;
						execution.Error = null;
					}

					job.State = JobState.Pending;
					job.WorkerId = null;
					job.LeaseExpiresAt = null;
					job.CompletedAt = null;
					job.ConcurrencyStamp = Guid.NewGuid();
				}

				await context.SaveChangesAsync(token);
				await transaction.CommitAsync(token);
			}, cancellationToken);
			if (count < BatchSize)
				return;
		}
	}
}

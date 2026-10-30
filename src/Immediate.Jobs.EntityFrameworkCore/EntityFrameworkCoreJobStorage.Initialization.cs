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
				var jobs = await context.Set<ImmediateJobEntity>().AsNoTracking()
					.Where(job => job.State == JobState.Active)
					.OrderBy(job => job.Id)
					.Take(BatchSize)
					.ToListAsync(token);
				count = jobs.Count;
				if (count == 0)
					return;
				var owned = context.Set<ImmediateJobEntity>().Where(MatchJobVersions(jobs));
				var executions = context.Set<ImmediateJobExecutionEntity>()
					.Where(execution => owned.Any(job => job.Id == execution.JobHandle && job.Attempt == execution.Attempt));
				var keys = (await executions.Select(execution => new { execution.JobHandle, execution.Attempt }).ToListAsync(token))
					.Select(static execution => (execution.JobHandle, execution.Attempt)).ToHashSet();
				foreach (var job in jobs)
				{
					if (!keys.Contains((job.Id, job.Attempt)) && JobExecutionRecord.CreateSynthetic(ToRecord(job)) is { } synthetic)
						context.Add(ToEntity(synthetic with { State = JobExecutionState.Interrupted, CompletedAt = now, Error = null }));
				}

				await executions.ExecuteUpdateAsync(update => update.SetProperty(execution => execution.State, JobExecutionState.Interrupted)
					.SetProperty(execution => execution.CompletedAt, now).SetProperty(execution => execution.Error, (string?)null), token);
				var stamp = Guid.NewGuid();
				var updated = await owned.ExecuteUpdateAsync(update => update.SetProperty(job => job.State, JobState.Pending)
					.SetProperty(job => job.WorkerId, (string?)null).SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
					.SetProperty(job => job.CompletedAt, (DateTimeOffset?)null).SetProperty(job => job.ConcurrencyStamp, stamp), token);
				if (updated != count)
					throw new DbUpdateConcurrencyException();

				await context.SaveChangesAsync(token);
				await transaction.CommitAsync(token);
			}, cancellationToken);
			if (count < BatchSize)
				return;
		}
	}
}

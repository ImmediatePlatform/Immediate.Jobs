using Immediate.Jobs.Shared.Apis;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	private async ValueTask InterruptActiveLeasesAsync(CancellationToken cancellationToken)
	{
		const int BatchSize = 1000;
		var now = timeProvider.GetUtcNow();
		while (true)
		{
			var count = 0;
			await RetryConcurrencyAsync(async connection =>
			{
				var jobs = await Jobs(connection)
					.Where(job => job.State == JobState.Active)
					.OrderBy(job => job.Id)
					.Take(BatchSize)
					.ToListAsync(cancellationToken);
				count = jobs.Count;
				foreach (var job in jobs)
				{
					if (await GetOrMaterializeExecutionAsync(connection, job, cancellationToken) is { })
					{
						await Executions(connection)
							.Where(execution => execution.JobHandle == job.Id && execution.Attempt == job.Attempt)
							.Set(execution => execution.State, JobExecutionState.Interrupted)
							.Set(execution => execution.CompletedAt, now)
							.Set(execution => execution.Error, (string?)null)
							.UpdateAsync(cancellationToken);
					}

					var stamp = job.ConcurrencyStamp;
					job.State = JobState.Pending;
					job.WorkerId = null;
					job.LeaseExpiresAt = null;
					job.CompletedAt = null;
					job.ConcurrencyStamp = Guid.NewGuid();
					if (!await UpdateJobAsync(connection, job, stamp, cancellationToken))
						throw new LostRaceException();
				}
			}, cancellationToken);
			if (count < BatchSize)
				return;
		}
	}
}

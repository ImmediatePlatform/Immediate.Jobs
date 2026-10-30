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
		const int BatchSize = 256;
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
				await MaterializeExecutionsAsync(connection, jobs, cancellationToken);
				var ids = jobs.Select(static job => job.Id).ToList();
				if (ids.Count == 0)
					return;
				await Executions(connection).Where(execution => Jobs(connection).Any(job =>
					ids.Contains(job.Id) && job.Attempt == execution.Attempt && job.Id == execution.JobHandle))
					.Set(execution => execution.State, JobExecutionState.Interrupted)
					.Set(execution => execution.CompletedAt, now)
					.Set(execution => execution.Error, (string?)null)
					.UpdateAsync(cancellationToken);
				var updated = await VersionedJobs(connection, jobs)
					.Set(job => job.State, JobState.Pending)
					.Set(job => job.WorkerId, (string?)null)
					.Set(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
					.Set(job => job.CompletedAt, (DateTimeOffset?)null)
					.Set(job => job.ConcurrencyStamp, Guid.NewGuid())
					.UpdateAsync(cancellationToken);
				if (updated != jobs.Count)
					throw new LostRaceException();
			}, cancellationToken);
			if (count < BatchSize)
				return;
		}
	}
}

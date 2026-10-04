using System.Data.Common;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireDueJobsAsync(
		JobAcquisitionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		AcquireDueJobsAsyncCalled(request.WorkerId, request.BatchSize, request.Queues.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		if (request.FairQueues is not null)
			return await AcquireDueJobsFairAsync(request, cancellationToken);

		var now = _timeProvider.GetUtcNow();
		var acquired = new List<JobRecord>(request.BatchSize);
		foreach (var queue in request.Queues)
		{
			var queueCapacity = Math.Min(queue.Capacity, request.BatchSize - acquired.Count);
			if (queueCapacity <= 0)
				continue;

			var jobCapacities = queue.JobCapacities.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
			while (queueCapacity > 0)
			{
				var eligibleNames = jobCapacities.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToList();
				if (eligibleNames.Count == 0)
					break;

				await using var readContext = await contextFactory.CreateDbContextAsync(cancellationToken);
				var candidates = await readContext.Set<ImmediateJobEntity>()
					.AsNoTracking()
					.Where(job => job.QueueName == queue.QueueName && eligibleNames.Contains(job.JobName) &&
						(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
							|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
					.OrderBy(job => job.DueAt)
					.ThenBy(job => job.CreatedAt)
					.ThenBy(job => job.Id)
					.Take(queueCapacity)
					.ToListAsync(cancellationToken);
				if (candidates.Count == 0)
					break;

				var selected = new List<ImmediateJobEntity>(candidates.Count);
				var selectionCapacities = new Dictionary<string, int>(jobCapacities, StringComparer.Ordinal);
				foreach (var candidate in candidates)
				{
					if (selectionCapacities[candidate.JobName] <= 0)
						continue;
					selectionCapacities[candidate.JobName]--;
					selected.Add(candidate);
				}

				var claimed = await AcquireCandidatesAsync(
					selected,
					request.WorkerId,
					request.Lease,
					now,
					cancellationToken
				);
				foreach (var job in claimed)
				{
					jobCapacities[job.JobName]--;
					queueCapacity--;
					acquired.Add(job);
				}

				if (claimed.Count == 0)
					break;
			}
		}

		return acquired;
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<JobHandle> jobHandles,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		AcquireJobsAsyncCalled(workerId, jobHandles.Count, lease);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		if (jobHandles.Count == 0)
			return [];

		var now = _timeProvider.GetUtcNow();
		var ids = jobHandles.Select(static job => job.Value).ToList();
		await using var readContext = await contextFactory.CreateDbContextAsync(cancellationToken);
		var candidates = await readContext.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.Where(job => ids.Contains(job.Id) &&
				(((job.State == JobState.Scheduled || job.State == JobState.Pending) && job.DueAt <= now)
				|| (job.State == JobState.Active && job.LeaseExpiresAt <= now)))
			.ToListAsync(cancellationToken);

		return await AcquireCandidatesAsync(candidates, workerId, lease, now, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask RenewLeaseAsync(
		JobHandle jobHandle,
		int executionNumber,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	)
	{
		RenewLeaseAsyncCalled(jobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await MutateOwnedAsync(
			jobHandle,
			executionNumber,
			workerId,
			(job, _) => job.LeaseExpiresAt = _timeProvider.GetUtcNow() + lease,
			cancellationToken
		);
	}

	private async ValueTask<IReadOnlyList<JobRecord>> AcquireCandidatesAsync(
		List<ImmediateJobEntity> candidates,
		string workerId,
		TimeSpan lease,
		DateTimeOffset now,
		CancellationToken cancellationToken
	)
	{
		var acquired = new List<JobRecord>(candidates.Count);
		foreach (var candidate in candidates)
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			var entity = Copy(candidate);
			_ = context.Attach(entity);
			await PrepareAcquisitionExecutionsAsync(context, candidate, workerId, now, cancellationToken);
			entity.State = JobState.Active;
			entity.WorkerId = workerId;
			entity.LeaseExpiresAt = now + lease;
			entity.Attempt++;
			entity.CompletedAt = null;
			entity.ExecutionTraceId = null;
			entity.ExecutionSpanId = null;
			entity.ExecutionStartedAt = null;
			entity.ConcurrencyStamp = Guid.NewGuid();
			if (entity.BatchHandle is { } batchHandle)
			{
				var batch = await context.Set<ImmediateJobBatchEntity>()
					.SingleOrDefaultAsync(item => item.Id == batchHandle, cancellationToken);
				if (batch is not null && batch.StartedAt is null)
				{
					batch.StartedAt = now;
					batch.ConcurrencyStamp = Guid.NewGuid();
				}
			}

			try
			{
				_ = await context.SaveChangesAsync(cancellationToken);
				acquired.Add(ToRecord(entity));
			}
			catch (DbUpdateException)
			{
				// Suppress only an expected optimistic-claim race; genuine provider failures remain visible.
				if (!await CandidateWasClaimedAsync(candidate, cancellationToken))
					throw;
			}
		}

		return acquired;
	}

	private async ValueTask<bool> CandidateWasClaimedAsync(
		ImmediateJobEntity candidate,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			var currentStamp = await context.Set<ImmediateJobEntity>()
				.AsNoTracking()
				.Where(job => job.Id == candidate.Id)
				.Select(static job => (Guid?)job.ConcurrencyStamp)
				.SingleOrDefaultAsync(cancellationToken);
			return currentStamp != candidate.ConcurrencyStamp;
		}
		catch (Exception exception) when (exception is DbException or InvalidOperationException)
		{
			return false;
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.AcquireDueJobsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.AcquireDueJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AcquireDueJobsAsync called (Worker={Worker}, BatchSize={BatchSize}, Queues={Queues})"
	)]
	private partial void AcquireDueJobsAsyncCalled(string worker, int batchSize, int queues);

	[LoggerMessage(
		EventId = LibraryEventIds.AcquireJobsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.AcquireJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AcquireJobsAsync called (Worker={Worker}, Jobs={Jobs}, Lease={Lease})"
	)]
	private partial void AcquireJobsAsyncCalled(string worker, int jobs, TimeSpan lease);

	[LoggerMessage(
		EventId = LibraryEventIds.RenewLeaseAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.RenewLeaseAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RenewLeaseAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void RenewLeaseAsyncCalled(JobHandle jobHandle, int execution);
}

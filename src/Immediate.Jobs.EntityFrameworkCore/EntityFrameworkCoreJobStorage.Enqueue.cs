using Immediate.Jobs.Shared.Apis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		EnqueueAsyncCalled(job.JobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await ExecuteWithStrategyAsync(
			operationCancellationToken => EnqueueCoreAsync(job, operationCancellationToken),
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(
		JobRecord job,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		EnqueueContinuationAsyncCalled(job.JobHandle, edges.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await ExecuteGraphInsertAsync(batch: null, [job], edges, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(
		BatchRecord batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken = default
	)
	{
		EnqueueBatchAsyncCalled(batch.BatchHandle, jobs.Count, edges.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await ExecuteGraphInsertAsync(batch, jobs, edges, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask AddBatchJobAsync(
		JobHandle currentJobHandle,
		int executionNumber,
		JobRecord job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		AddBatchJobAsyncCalled(job.JobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(
			operationCancellationToken => AddBatchJobCoreAsync(
				currentJobHandle,
				executionNumber,
				job,
				options,
				operationCancellationToken
			),
			cancellationToken
		);
	}

	private async Task EnqueueCoreAsync(JobRecord job, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

		if (job.GroupId is { } groupId && !await HasLiveGroupJobsAsync(
			context,
			job.QueueName,
			groupId,
			cancellationToken
		))
		{
			var cursor = await context.Set<ImmediateFairQueueGroupEntity>()
				.SingleOrDefaultAsync(
					group => group.QueueName == job.QueueName && group.GroupId == groupId,
					cancellationToken
				);
			if (cursor is not null)
				_ = context.Remove(cursor);
		}

		_ = context.Set<ImmediateJobEntity>().Add(ToEntity(job));
		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
	}

	private ValueTask ExecuteGraphInsertAsync(
		BatchRecord? batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken
	) => RetryConcurrencyAsync(
		operationCancellationToken => InsertGraphCoreAsync(batch, jobs, edges, operationCancellationToken),
		cancellationToken
	);

	private async Task InsertGraphCoreAsync(
		BatchRecord? batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken
	)
	{
		var jobHandles = jobs.Select(static job => job.JobHandle.Value).ToHashSet(StringComparer.Ordinal);
		if (jobHandles.Count != jobs.Count)
			throw new ImmediateJobException("A batch or continuation insert contains duplicate job identifiers.");
		if (batch is not null && jobs.Any(job => job.BatchHandle != batch.BatchHandle))
			throw new ImmediateJobException("Every atomic batch member must carry the committed batch identifier.");

		var edgeEntities = edges.Select(ToEntity).ToList();
		if (edgeEntities.Any(edge => !jobHandles.Contains(edge.ChildJobHandle)))
			throw new ImmediateJobException("Every continuation edge must target a job inserted by the same operation.");
		if (edgeEntities.DistinctBy(static edge => (edge.ChildJobHandle, edge.ParentKind, edge.ParentId)).Count() != edgeEntities.Count)
			throw new ImmediateJobException("Duplicate continuation edges are not allowed.");
		ThrowIfCyclic(jobHandles, edgeEntities);

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var jobEntities = jobs.Select(ToEntity).ToDictionary(static job => job.Id, StringComparer.Ordinal);
		await EvaluateInitialDependenciesAsync(
			context,
			jobEntities,
			edgeEntities,
			_timeProvider.GetUtcNow(),
			cancellationToken
		);

		if (batch is not null)
		{
			var terminal = jobEntities.Values.Where(static job => IsTerminal(job.State)).ToList();
			var pending = jobEntities.Count - terminal.Count;
			var succeeded = terminal.Count(static job => job.State == JobState.Succeeded);
			var failed = terminal.Count(static job => job.State == JobState.Failed);
			var cancelled = terminal.Count(static job => job.State == JobState.Cancelled);
			var skipped = terminal.Count(static job => job.State == JobState.Skipped);
			_ = context.Add(new ImmediateJobBatchEntity
			{
				Id = batch.BatchHandle.Value,
				CreatedAt = batch.CreatedAt,
				TotalJobs = jobEntities.Count,
				PendingCount = pending,
				SucceededCount = succeeded,
				FailedCount = failed,
				CancelledCount = cancelled,
				SkippedCount = skipped,
				StartedAt = batch.StartedAt,
				CompletedAt = pending == 0 ? batch.CompletedAt ?? _timeProvider.GetUtcNow() : null,
				State = pending == 0
					? GetTerminalBatchState(failed, cancelled)
					: batch.State == BatchState.WaitingForTrigger ? BatchState.WaitingForTrigger : BatchState.Executing,
				ConcurrencyStamp = Guid.NewGuid(),
			});
		}

		context.AddRange(jobEntities.Values);
		context.AddRange(edgeEntities);
		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
	}

	private async Task AddBatchJobCoreAsync(
		JobHandle currentJobHandle,
		int executionNumber,
		JobRecord record,
		ContinuationOptions options,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var current = await context.Set<ImmediateJobEntity>()
			.SingleOrDefaultAsync(job => job.Id == currentJobHandle.Value && job.Attempt == executionNumber && job.State == JobState.Active, cancellationToken)
			?? throw new ImmediateJobException($"The current active job '{currentJobHandle}' was not found.");
		if (current.BatchHandle is not { } batchHandle)
			throw new ImmediateJobException("The current job does not belong to a batch.");
		if (options is not (ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations))
			throw new ArgumentOutOfRangeException(nameof(options));
		if (!string.Equals(record.BatchHandle?.Value, batchHandle, StringComparison.Ordinal))
			throw new ImmediateJobException("The new job must belong to the current job's batch.");
		if (record.State is JobState.Active or JobState.AwaitingContinuation || IsTerminal(record.State))
			throw new ImmediateJobException($"Concurrent batch member '{record.JobHandle}' has invalid state '{record.State}'.");

		var batch = await context.Set<ImmediateJobBatchEntity>()
			.SingleAsync(item => item.Id == batchHandle && item.State == BatchState.Executing, cancellationToken);
		var job = ToEntity(record);
		_ = context.Add(job);
		batch.TotalJobs++;
		batch.PendingCount++;
		batch.ConcurrencyStamp = Guid.NewGuid();

		if (options == ContinuationOptions.BeforeContinuations)
		{
			var waiters = await GetActiveWaitersAsync(context, currentJobHandle, cancellationToken);
			foreach (var waiter in waiters)
			{
				_ = context.Add(new ImmediateJobContinuationEntity
				{
					ChildJobHandle = waiter.Id,
					ParentKind = ContinuationParentKind.Job,
					ParentId = job.Id,
					Delay = 0,
					Trigger = ContinuationTrigger.Success,
				});
				waiter.RemainingDependencies++;
				waiter.ConcurrencyStamp = Guid.NewGuid();
			}
		}

		_ = await context.SaveChangesAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.EnqueueAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueAsync called (JobHandle={JobHandle})"
	)]
	private partial void EnqueueAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueContinuationAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.EnqueueContinuationAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueContinuationAsync called (JobHandle={JobHandle}, Edges={Edges})"
	)]
	private partial void EnqueueContinuationAsyncCalled(JobHandle jobHandle, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueBatchAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.EnqueueBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueBatchAsync called (BatchHandle={BatchHandle}, Jobs={Jobs}, Edges={Edges})"
	)]
	private partial void EnqueueBatchAsyncCalled(BatchHandle batchHandle, int jobs, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.AddBatchJobAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.AddBatchJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AddBatchJobAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void AddBatchJobAsyncCalled(JobHandle jobHandle, int execution);
}

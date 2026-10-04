using Immediate.Jobs.Shared.Apis;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default)
	{
		EnqueueAsyncCalled(job.JobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		_ = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			await ResetReturningGroupCursorsAsync(connection, [job], cancellationToken);
			_ = await InsertAsync(connection, ToEntity(job), cancellationToken);
			await connection.CommitTransactionAsync(cancellationToken);
		}
		catch
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			throw;
		}
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
			connection => AddBatchJobCoreAsync(connection, currentJobHandle, executionNumber, job, options, cancellationToken),
			cancellationToken
		);
	}

	private ValueTask ExecuteGraphInsertAsync(
		BatchRecord? batch,
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<JobContinuationEdge> edges,
		CancellationToken cancellationToken
	) => RetryConcurrencyAsync(
		connection => InsertGraphCoreAsync(connection, batch, jobs, edges, cancellationToken),
		cancellationToken
	);

	private async Task InsertGraphCoreAsync(
		DataConnection connection,
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

		var jobEntities = jobs.Select(ToEntity).ToDictionary(static job => job.Id, StringComparer.Ordinal);
		await ResetReturningGroupCursorsAsync(connection, jobs, cancellationToken);
		await EvaluateInitialDependenciesAsync(
			connection,
			jobEntities,
			edgeEntities,
			timeProvider.GetUtcNow(),
			cancellationToken
		);

		if (batch is not null)
		{
			var terminal = jobEntities.Values.Where(static job => IsTerminal(job.State)).ToList();
			var pending = jobEntities.Count - terminal.Count;
			var failed = terminal.Count(static job => job.State == JobState.Failed);
			var cancelled = terminal.Count(static job => job.State == JobState.Cancelled);
			var skipped = terminal.Count(static job => job.State == JobState.Skipped);
			_ = await InsertAsync(connection, new ImmediateJobBatchEntity
			{
				Id = batch.BatchHandle.Value,
				CreatedAt = batch.CreatedAt,
				TotalJobs = jobEntities.Count,
				PendingCount = pending,
				SucceededCount = terminal.Count(static job => job.State == JobState.Succeeded),
				FailedCount = failed,
				CancelledCount = cancelled,
				SkippedCount = skipped,
				StartedAt = batch.StartedAt,
				CompletedAt = pending == 0 ? batch.CompletedAt ?? timeProvider.GetUtcNow() : null,
				State = pending == 0
					? GetTerminalBatchState(failed, cancelled)
					: batch.State == BatchState.WaitingForTrigger ? BatchState.WaitingForTrigger : BatchState.Executing,
				ConcurrencyStamp = Guid.NewGuid(),
			}, cancellationToken);
		}

		foreach (var entity in jobEntities.Values)
			_ = await InsertAsync(connection, entity, cancellationToken);
		foreach (var edge in edgeEntities)
			_ = await InsertAsync(connection, edge, cancellationToken);
	}

	private static void ValidateDynamicJob(JobRecord job, string description)
	{
		if (job.State is not (JobState.Pending or JobState.Scheduled))
			throw new ImmediateJobException($"{description} '{job.JobHandle}' has invalid state '{job.State}'.");
	}

	private async Task AddBatchJobCoreAsync(
		DataConnection connection,
		JobHandle currentJobHandle,
		int executionNumber,
		JobRecord record,
		ContinuationOptions options,
		CancellationToken cancellationToken
	)
	{
		var current = await Jobs(connection)
			.SingleOrDefaultAsync(job => job.Id == currentJobHandle.Value && job.Attempt == executionNumber && job.State == JobState.Active, cancellationToken)
			?? throw new ImmediateJobException($"The current active job '{currentJobHandle}' was not found.");
		if (current.BatchHandle is not { } batchHandle)
			throw new ImmediateJobException("The current job does not belong to a batch.");
		ValidateDynamicJob(record, "Concurrent batch member");
		if (!string.Equals(record.BatchHandle?.Value, batchHandle, StringComparison.Ordinal))
			throw new ImmediateJobException("The new job must belong to the current job's batch.");
		var batch = await Batches(connection)
			.SingleAsync(item => item.Id == batchHandle && item.State == BatchState.Executing, cancellationToken);
		await ResetReturningGroupCursorsAsync(connection, [record], cancellationToken);
		var job = ToEntity(record);
		_ = await InsertAsync(connection, job, cancellationToken);
		var batchStamp = batch.ConcurrencyStamp;
		batch.TotalJobs++;
		batch.PendingCount++;
		batch.ConcurrencyStamp = Guid.NewGuid();
		if (!await UpdateBatchAsync(connection, batch, batchStamp, cancellationToken))
			throw new LostRaceException();

		if (options != ContinuationOptions.BeforeContinuations)
			return;
		var waiters = await GetActiveWaitersAsync(connection, currentJobHandle, cancellationToken);
		foreach (var waiter in waiters)
		{
			_ = await InsertAsync(connection, new ImmediateJobContinuationEntity
			{
				ChildJobHandle = waiter.Id,
				ParentKind = ContinuationParentKind.Job,
				ParentId = job.Id,
				Delay = 0,
				Trigger = ContinuationTrigger.Success,
			}, cancellationToken);
			var waiterStamp = waiter.ConcurrencyStamp;
			waiter.RemainingDependencies++;
			waiter.ConcurrencyStamp = Guid.NewGuid();
			if (!await UpdateJobAsync(connection, waiter, waiterStamp, cancellationToken))
				throw new LostRaceException();
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.EnqueueAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueAsync called (JobHandle={JobHandle})"
	)]
	private partial void EnqueueAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueContinuationAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.EnqueueContinuationAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueContinuationAsync called (JobHandle={JobHandle}, Edges={Edges})"
	)]
	private partial void EnqueueContinuationAsyncCalled(JobHandle jobHandle, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.EnqueueBatchAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.EnqueueBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueBatchAsync called (BatchHandle={BatchHandle}, Jobs={Jobs}, Edges={Edges})"
	)]
	private partial void EnqueueBatchAsyncCalled(BatchHandle batchHandle, int jobs, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.AddBatchJobAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.AddBatchJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AddBatchJobAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void AddBatchJobAsyncCalled(JobHandle jobHandle, int execution);
}

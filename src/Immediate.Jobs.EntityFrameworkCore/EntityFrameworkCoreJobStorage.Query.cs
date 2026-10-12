using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		GetMonitoringSnapshotAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var rawCounts = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.GroupBy(job => job.State)
			.Select(group => new { State = group.Key, Count = group.LongCount() })
			.ToListAsync(cancellationToken);
		var counts = Enum.GetValues<JobState>().ToDictionary(static state => state, static _ => 0L);
		foreach (var item in rawCounts)
			counts[item.State] = item.Count;

		var recurring = await context.Set<ImmediateRecurringJobEntity>()
			.AsNoTracking()
			.OrderBy(schedule => schedule.Name)
			.Select(schedule => new RecurringJobSchedule
			{
				Name = schedule.Name,
				JobName = schedule.JobName,
				QueueName = schedule.QueueName,
				Cron = schedule.Cron,
				TimeZone = schedule.TimeZone,
				IsCodeDefined = schedule.IsCodeDefined,
				IsPaused = schedule.IsPaused,
				NextRunAt = schedule.NextRunAt,
				LastRunAt = schedule.LastRunAt,
			})
			.ToListAsync(cancellationToken);
		var now = _timeProvider.GetUtcNow();
		var serverEntities = await context.Set<ImmediateJobServerEntity>()
			.AsNoTracking()
			.Where(server => server.ExpiresAt >= now)
			.OrderBy(server => server.WorkerId)
			.ToListAsync(cancellationToken);
		IReadOnlyList<JobServerSnapshot> servers =
		[
			.. serverEntities.Select(server =>
				JsonSerializer.Deserialize(server.Details, EntityFrameworkCoreJsonSerializerContext.Default.JobServerSnapshot)!),
		];
		return new JobMonitoringSnapshot
		{
			CapturedAt = _timeProvider.GetUtcNow(),
			Counts = counts,
			Recurring = recurring,
			Servers = servers,
			Capabilities = this.GetCapabilities(),
		};
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
	{
		QueryJobsAsyncCalled(query);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var jobs = context.Set<ImmediateJobEntity>().AsNoTracking();
		if (query.JobHandle is { Value: { } id })
			jobs = jobs.Where(job => job.Id == id);
		if (query.State is { } state)
			jobs = jobs.Where(job => job.State == state);
		if (!string.IsNullOrWhiteSpace(query.QueueName))
			jobs = jobs.Where(job => job.QueueName == query.QueueName);
		if (!string.IsNullOrWhiteSpace(query.JobName))
			jobs = jobs.Where(job => job.JobName == query.JobName);
		if (query.CreatedBefore is { } createdBefore)
			jobs = jobs.Where(job => job.CreatedAt < createdBefore);
		if (!string.IsNullOrWhiteSpace(query.Search))
		{
			var search = query.Search.ToUpperInvariant();
			// The parameterless form is intentionally used because relational providers translate it to SQL.
#pragma warning disable CA1304, CA1311, CA1862, MA0011
			jobs = context.Database.ProviderName!.Contains("Npgsql", StringComparison.Ordinal)
				? jobs.Where(job => EF.Functions.Collate(job.JobName, "und-x-icu").ToUpper().Contains(search))
				: jobs.Where(job => job.JobName.ToUpper().Contains(search));
#pragma warning restore CA1304, CA1311, CA1862, MA0011
		}

		var entities = await jobs.OrderByDescending(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken);
		return [.. entities.Select(ToRecord)];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryNonCompletedJobsAsync(
		string jobName,
		CancellationToken cancellationToken = default
	)
	{
		QueryNonCompletedJobsAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var jobs = context.Set<ImmediateJobEntity>().AsNoTracking();

		var entities = await jobs
			.Where(job => job.JobName == jobName)
			.Where(
				job =>
					job.State == JobState.AwaitingContinuation
					|| job.State == JobState.WaitingForTrigger
					|| job.State == JobState.Scheduled
					|| job.State == JobState.Pending
					|| job.State == JobState.Active
			)
			.ToListAsync(cancellationToken);

		return [.. entities.Select(ToRecord)];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobHandle,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryJobExecutionsAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var job = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken);
		if (job is null)
			return [];

		var executions = context.Set<ImmediateJobExecutionEntity>()
			.AsNoTracking()
			.Where(execution => execution.JobHandle == jobHandle.Value);
		if (query.Attempt is { } attempt)
			executions = executions.Where(execution => execution.Attempt == attempt);

		var synthetic = JobExecutionRecord.CreateSynthetic(ToRecord(job));
		var syntheticMissing = synthetic is not null
			&& (query.Attempt is null || query.Attempt == synthetic.Attempt)
			&& !await executions.AnyAsync(
				execution => execution.Attempt == synthetic.Attempt,
				cancellationToken
			);
		var skip = query.Skip;
		var take = query.Take;
		var result = new List<JobExecutionRecord>(take);
		if (syntheticMissing && skip == 0 && take != 0)
		{
			result.Add(synthetic!);
			take--;
		}
		else if (syntheticMissing)
		{
			skip--;
		}

		if (take != 0 && skip >= 0)
		{
			var persisted = await executions
				.OrderByDescending(execution => execution.Attempt)
				.Skip(skip)
				.Take(take)
				.ToListAsync(cancellationToken);
			result.AddRange(persisted.Select(ToRecord));
		}

		return result;
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchStatusAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		GetBatchStatusAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var batch = await context.Set<ImmediateJobBatchEntity>()
			.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken);
		return batch is null ? null : ToStatus(batch);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(
		IReadOnlyCollection<JobHandle> childJobHandles,
		CancellationToken cancellationToken = default
	)
	{
		GetIncomingEdgesAsyncCalled(childJobHandles.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		if (childJobHandles.Count == 0)
			return [];

		var ids = childJobHandles.Select(static job => job.Value).Distinct(StringComparer.Ordinal).ToList();
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var edges = await context.Set<ImmediateJobContinuationEntity>()
			.AsNoTracking()
			.Where(edge => ids.Contains(edge.ChildJobHandle))
			.OrderBy(edge => edge.ChildJobHandle)
			.ThenBy(edge => edge.ParentKind)
			.ThenBy(edge => edge.ParentId)
			.ToListAsync(cancellationToken);
		return [.. edges.Select(ToContinuationEdge)];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryBatchesAsyncCalled(query);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var batches = context.Set<ImmediateJobBatchEntity>().AsNoTracking();
		if (query.State is { } state)
			batches = batches.Where(batch => batch.State == state);
		var entities = await batches.OrderByDescending(batch => batch.CreatedAt)
			.ThenBy(batch => batch.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken);
		return [.. entities.Select(ToStatus)];
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		BatchHandle batchHandle,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryBatchMembersAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var jobs = context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.Where(job => job.BatchHandle == batchHandle.Value);
		if (query.State is { } state)
			jobs = jobs.Where(job => job.State == state);
		return await jobs.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.Select(job => new BatchMemberStatus
			{
				JobHandle = JobHandle.FromString(job.Id),
				JobName = job.JobName,
				QueueName = job.QueueName,
				State = job.State,
				Attempt = job.Attempt,
				CreatedAt = job.CreatedAt,
				CompletedAt = job.CompletedAt,
				LastError = job.LastError,
			})
			.ToListAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		GetBatchGraphAsyncCalled(batchHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		if (!await context.Set<ImmediateJobBatchEntity>()
			.AnyAsync(batch => batch.Id == batchHandle.Value, cancellationToken))
		{
			return null;
		}

		var jobs = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.Where(job => job.BatchHandle == batchHandle.Value)
			.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Select(job => new BatchGraphNode { JobHandle = JobHandle.FromString(job.Id), JobName = job.JobName, State = job.State })
			.ToListAsync(cancellationToken);
		var ids = jobs.Select(static job => job.JobHandle.Value).ToList();
		var edges = ids.Count == 0
			? []
			: await context.Set<ImmediateJobContinuationEntity>()
				.AsNoTracking()
				.Where(edge => ids.Contains(edge.ChildJobHandle))
				.OrderBy(edge => edge.ChildJobHandle)
				.ThenBy(edge => edge.ParentKind)
				.ThenBy(edge => edge.ParentId)
				.ToListAsync(cancellationToken);
		return new BatchGraph { BatchHandle = batchHandle, Nodes = jobs, Edges = [.. edges.Select(ToContinuationEdge)] };
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobHandle,
		CancellationToken cancellationToken = default
	)
	{
		GetJobStatusAsyncCalled(jobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var job = await context.Set<ImmediateJobEntity>()
			.AsNoTracking()
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken);
		if (job is null)
			return null;
		var edges = await context.Set<ImmediateJobContinuationEntity>()
			.AsNoTracking()
			.Where(edge => edge.ChildJobHandle == jobHandle.Value)
			.OrderBy(edge => edge.ParentKind)
			.ThenBy(edge => edge.ParentId)
			.ToListAsync(cancellationToken);
		return new JobStatus
		{
			JobHandle = JobHandle.FromString(job.Id),
			JobName = job.JobName,
			QueueName = job.QueueName,
			State = job.State,
			Attempt = job.Attempt,
			MaxAttempts = 0,
			CreatedAt = job.CreatedAt,
			DueAt = job.DueAt,
			CompletedAt = job.CompletedAt,
			LastError = job.LastError,
			BatchHandle = BatchHandle.FromString(job.BatchHandle),
			DependsOn = [.. edges.Select(ToContinuationEdge)],
		};
	}

	[LoggerMessage(
		EventId = LibraryEventIds.GetMonitoringSnapshotAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.GetMonitoringSnapshotAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetMonitoringSnapshotAsync called"
	)]
	private partial void GetMonitoringSnapshotAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.QueryJobsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.QueryJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobsAsync called (Query={Query})"
	)]
	private partial void QueryJobsAsyncCalled(JobQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryNonCompletedJobsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.QueryNonCompletedJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryNonCompletedJobsAsyncCalled called (JobName={JobName})"
	)]
	private partial void QueryNonCompletedJobsAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryJobExecutionsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.QueryJobExecutionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobExecutionsAsync called (JobHandle={JobHandle})"
	)]
	private partial void QueryJobExecutionsAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetBatchStatusAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.GetBatchStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetBatchStatusAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void GetBatchStatusAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetIncomingEdgesAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.GetIncomingEdgesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetIncomingEdgesAsync called (Jobs={Jobs})"
	)]
	private partial void GetIncomingEdgesAsyncCalled(int jobs);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryBatchesAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.QueryBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryBatchesAsync called (Query={Query})"
	)]
	private partial void QueryBatchesAsyncCalled(BatchQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryBatchMembersAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.QueryBatchMembersAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryBatchMembersAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void QueryBatchMembersAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetBatchGraphAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.GetBatchGraphAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetBatchGraphAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void GetBatchGraphAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobStatusAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.GetJobStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobStatusAsync called (JobHandle={JobHandle})"
	)]
	private partial void GetJobStatusAsyncCalled(JobHandle jobHandle);
}

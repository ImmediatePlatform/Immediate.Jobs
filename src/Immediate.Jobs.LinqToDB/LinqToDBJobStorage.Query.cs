using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobContinuationEdge>> GetIncomingEdgesAsync(
		IReadOnlyCollection<JobHandle> childJobHandles,
		CancellationToken cancellationToken = default
	)
	{
		GetIncomingEdgesAsyncCalled(childJobHandles.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		var ids = childJobHandles.Select(i => i.Value).Distinct(StringComparer.Ordinal).ToList();

		await using var scope = contextScope.GetScope(out var connection);

		var edges = await Continuations(connection)
			.Where(edge => ids.Contains(edge.ChildJobHandle))
			.OrderBy(edge => edge.ChildJobHandle)
			.ThenBy(edge => edge.ParentKind)
			.ThenBy(edge => edge.ParentId)
			.ToListAsync(cancellationToken);
		return [.. edges.Select(ToContinuationEdge)];
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(
		CancellationToken cancellationToken = default
	)
	{
		GetMonitoringSnapshotAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var rawCounts = await Jobs(connection)
			.GroupBy(job => job.State)
			.Select(group => new { State = group.Key, Count = group.LongCount() })
			.ToListAsync(cancellationToken);
		var counts = Enum.GetValues<JobState>().ToDictionary(static state => state, static _ => 0L);
		foreach (var item in rawCounts)
			counts[item.State] = item.Count;

		var recurringEntities = await Recurring(connection)
			.OrderBy(schedule => schedule.Name)
			.ToListAsync(cancellationToken);
		var now = timeProvider.GetUtcNow();
		var serverEntities = await Servers(connection)
			.Where(server => server.ExpiresAt >= now)
			.OrderBy(server => server.WorkerId)
			.ToListAsync(cancellationToken);
		return new JobMonitoringSnapshot
		{
			CapturedAt = timeProvider.GetUtcNow(),
			Counts = counts,
			Recurring = [.. recurringEntities.Select(ToRecord)],
			Servers =
			[
				.. serverEntities.Select(server =>
					JsonSerializer.Deserialize(server.Details, LinqToDBJsonSerializerContext.Default.JobServerSnapshot)!),
			],
			Capabilities = this.GetCapabilities(),
		};
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryJobsAsyncCalled(query);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		IQueryable<ImmediateJobEntity> jobs = Jobs(connection);
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
#pragma warning disable CA1304, CA1311, CA1862, MA0011
			jobs = connection.DataProvider.Name.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase)
				? jobs.Where(job => Sql.Collate(job.JobName, "und-x-icu").ToUpper().Contains(search))
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

		await using var scope = contextScope.GetScope(out var connection);

		var entities = await Jobs(connection)
			.Where(job => job.JobName == jobName)
			.Where(job => job.State.In(
				JobState.AwaitingContinuation,
				JobState.WaitingForTrigger,
				JobState.Scheduled,
				JobState.Pending,
				JobState.Active
			))
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

		await using var scope = contextScope.GetScope(out var connection);

		var job = await Jobs(connection)
			.SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken);

		if (job is null)
			return [];

		var executions = Executions(connection)
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
		if (syntheticMissing && skip == 0)
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

		await using var scope = contextScope.GetScope(out var connection);

		var batch = await Batches(connection).SingleOrDefaultAsync(item => item.Id == batchHandle.Value, cancellationToken);
		return batch is null ? null : ToStatus(batch);
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

		await using var scope = contextScope.GetScope(out var connection);

		IQueryable<ImmediateJobBatchEntity> batches = Batches(connection);
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

		await using var scope = contextScope.GetScope(out var connection);

		var jobs = Jobs(connection).Where(job => job.BatchHandle == batchHandle.Value);
		if (query.State is { } state)
			jobs = jobs.Where(job => job.State == state);

		var entities = await jobs.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.Skip(query.Skip)
			.Take(query.Take)
			.ToListAsync(cancellationToken);

		return entities
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
			.ToList();
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

		await using var scope = contextScope.GetScope(out var connection);

		if (!await Batches(connection).AnyAsync(batch => batch.Id == batchHandle.Value, cancellationToken))
			return null;
		var entities = await Jobs(connection)
			.Where(job => job.BatchHandle == batchHandle.Value)
			.OrderBy(job => job.CreatedAt)
			.ThenBy(job => job.Id)
			.ToListAsync(cancellationToken);
		var jobs = entities.Select(job => new BatchGraphNode { JobHandle = JobHandle.FromString(job.Id), JobName = job.JobName, State = job.State }).ToList();
		var edges = entities.Count == 0
			? []
			: await Continuations(connection)
				.Where(edge => edge.ChildJobHandle.In(entities.Select(static job => job.Id)))
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

		await using var scope = contextScope.GetScope(out var connection);

		var job = await Jobs(connection).SingleOrDefaultAsync(item => item.Id == jobHandle.Value, cancellationToken);
		if (job is null)
			return null;
		var edges = await Continuations(connection)
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
		EventId = LibraryEventIds.GetIncomingEdgesAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetIncomingEdgesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetIncomingEdgesAsync called (Jobs={Jobs})"
	)]
	private partial void GetIncomingEdgesAsyncCalled(int jobs);

	[LoggerMessage(
		EventId = LibraryEventIds.GetMonitoringSnapshotAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetMonitoringSnapshotAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetMonitoringSnapshotAsync called"
	)]
	private partial void GetMonitoringSnapshotAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.QueryJobsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.QueryJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobsAsync called (Query={Query})"
	)]
	private partial void QueryJobsAsyncCalled(JobQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryNonCompletedJobsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.QueryNonCompletedJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryNonCompletedJobsAsyncCalled called (JobName={JobName})"
	)]
	private partial void QueryNonCompletedJobsAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryJobExecutionsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.QueryJobExecutionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobExecutionsAsync called (JobHandle={JobHandle})"
	)]
	private partial void QueryJobExecutionsAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetBatchStatusAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetBatchStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetBatchStatusAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void GetBatchStatusAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryBatchesAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.QueryBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryBatchesAsync called (Query={Query})"
	)]
	private partial void QueryBatchesAsyncCalled(BatchQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.QueryBatchMembersAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.QueryBatchMembersAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryBatchMembersAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void QueryBatchMembersAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetBatchGraphAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetBatchGraphAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetBatchGraphAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void GetBatchGraphAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobStatusAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetJobStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobStatusAsync called (JobHandle={JobHandle})"
	)]
	private partial void GetJobStatusAsyncCalled(JobHandle jobHandle);
}

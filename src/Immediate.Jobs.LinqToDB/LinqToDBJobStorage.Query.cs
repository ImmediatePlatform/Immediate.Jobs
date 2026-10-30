using System.Linq.Expressions;
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
	public async ValueTask<JobMonitoringDefinitions> GetMonitoringDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		GetMonitoringDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await using var scope = contextScope.GetScope(out var connection);
		var recurring = await Recurring(connection).OrderBy(schedule => schedule.Name)
			.Select(schedule => new RecurringJobDefinition
			{
				Name = schedule.Name,
				JobName = schedule.JobName,
				Cron = schedule.Cron,
				TimeZone = schedule.TimeZone,
				IsCodeDefined = schedule.IsCodeDefined,
			}).ToListAsync(cancellationToken);
		return new() { Jobs = await ReadJobDefinitionsAsync(connection, cancellationToken), Recurring = recurring };
	}

	private async Task<IReadOnlyList<JobAcquisitionState>> ReadDefinitionStatusesAsync(
		DataConnection connection,
		IQueryable<ImmediateJobDefinitionMetadataEntity> metadata,
		DateTimeOffset now,
		CancellationToken cancellationToken,
		bool includeActiveCount = true
	)
	{
		var snapshots = await (
			from item in metadata
			join usage in DefinitionStates(connection) on item.Name equals usage.JobName into usages
			from usage in usages.DefaultIfEmpty()
			select new
			{
				Metadata = item,
				IsPaused = usage != null && usage.IsPaused,
				FixedWindowStart = usage == null ? null : usage.FixedWindowStart,
				FixedWindowCount = usage == null ? 0 : usage.FixedWindowCount,
				ActiveCount = includeActiveCount || item.MaxConcurrency > 0
					? Jobs(connection).Count(job => job.JobName == item.Name && job.State == JobState.Active && job.LeaseExpiresAt > now)
					: 0,
			}).ToListAsync(cancellationToken);
		var definitions = snapshots.OrderBy(static snapshot => snapshot.Metadata.Name, StringComparer.OrdinalIgnoreCase)
			.Select(snapshot => new { State = snapshot, Definition = ToRecord(snapshot.Metadata) }).ToList();
		var windows = definitions.Where(static item => item.Definition.SlidingWindowPeriod != null)
			.GroupBy(static item => item.Definition.SlidingWindowPeriod!.Value).ToList();
		Expression<Func<ImmediateJobAcquisitionEntity, bool>> predicate = static _ => false;
		foreach (var window in windows)
		{
			var names = window.Select(static item => item.Definition.Name).ToList();
			var cutoff = now - window.Key;
			Expression<Func<ImmediateJobAcquisitionEntity, bool>> inWindow = acquisition =>
				names.Contains(acquisition.JobName) && acquisition.AcquiredAt > cutoff;
			predicate = Expression.Lambda<Func<ImmediateJobAcquisitionEntity, bool>>(
				Expression.OrElse(predicate.Body, Expression.Invoke(inWindow, predicate.Parameters)), predicate.Parameters);
		}

		var acquisitions = windows.Count == 0 ? [] : await Acquisitions(connection)
			.Where(predicate).OrderBy(acquisition => acquisition.AcquiredAt)
			.Select(acquisition => new { acquisition.JobName, acquisition.AcquiredAt })
			.ToListAsync(cancellationToken);
		var byName = acquisitions.ToLookup(static acquisition => acquisition.JobName, StringComparer.OrdinalIgnoreCase);
		return [.. definitions.Select(item => JobAcquisitionEvaluator.Evaluate(
			item.Definition.Name, item.State.IsPaused, item.Definition.AcquisitionLimits, now,
			byName[item.Definition.Name].Select(static acquisition => acquisition.AcquiredAt).ToList(),
			item.State.FixedWindowStart, item.State.FixedWindowCount, item.State.ActiveCount))];
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

		var rawCounts = await JobsWithDefinitions(connection)
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
			DefinitionStatuses = await ReadDefinitionStatusesAsync(connection, Definitions(connection), now, cancellationToken),
			Recurring = [.. recurringEntities.Select(static schedule => new RecurringJobStatus
			{
				Name = schedule.Name,
				IsPaused = schedule.IsPaused,
				CancellationReason = schedule.CancellationReason,
				NextRunAt = schedule.NextRunAt,
				LastRunAt = schedule.LastRunAt,
			})],
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

		IQueryable<JobQueryRow> jobs = JobsWithDefinitions(connection);
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

		// Page narrow keys before fetching payloads, so skipped rows need no table lookups.
		var page = jobs.OrderByDescending(job => job.CreatedAt).ThenBy(job => job.Id)
			.Skip(query.Skip).Take(query.Take).Select(job => job.Id);
		var entities = await (
			from pageId in page
			join job in JobsWithDefinitions(connection) on pageId equals job.Id
			orderby job.CreatedAt descending, job.Id
			select job
		).ToListAsync(cancellationToken);
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

		var entities = await JobsWithDefinitions(connection)
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

		var jobs = Jobs(connection).Where(job => job.Id == jobHandle.Value);
		var persisted = Executions(connection)
			.Where(execution => execution.JobHandle == jobHandle.Value && jobs.Any())
			.Select(execution => new
			{
				execution.JobHandle,
				execution.Attempt,
				State = (short)execution.State,
				execution.WorkerId,
				execution.AcquiredAt,
				execution.ExecutionStartedAt,
				execution.CompletedAt,
				execution.ExecutionTraceId,
				execution.ExecutionSpanId,
				execution.Error,
				execution.IsSynthetic,
			});
		// A scalar lookup keeps SQL Server's latest-attempt probe correlated to both
		// primary-key columns instead of scanning a job's entire history for an anti join.
		var synthetic = jobs.Where(job => job.Attempt > 0 && Executions(connection)
			.Where(execution => execution.JobHandle == job.Id && execution.Attempt == job.Attempt)
			.Select(execution => (int?)execution.Attempt).FirstOrDefault() == null)
			.Select(job => new
			{
				JobHandle = job.Id,
				job.Attempt,
				State = (short)(job.State == JobState.Active ? JobExecutionState.Active
					: job.State == JobState.Succeeded ? JobExecutionState.Succeeded
					: job.State == JobState.Failed || ((job.State == JobState.Pending || job.State == JobState.Scheduled) && job.LastError != null) ? JobExecutionState.Failed
					: job.State == JobState.Cancelled ? JobExecutionState.Cancelled : JobExecutionState.Interrupted),
				WorkerId = job.State == JobState.Active ? job.WorkerId : null,
				AcquiredAt = (DateTimeOffset?)null,
				job.ExecutionStartedAt,
				CompletedAt = job.State == JobState.Succeeded || job.State == JobState.Failed || job.State == JobState.Cancelled || job.State == JobState.Skipped ? job.CompletedAt : null,
				job.ExecutionTraceId,
				job.ExecutionSpanId,
				Error = job.State == JobState.Failed || ((job.State == JobState.Pending || job.State == JobState.Scheduled) && job.LastError != null) ? job.LastError : null,
				IsSynthetic = true,
			});
		if (query.Attempt is { } attempt)
		{
			persisted = persisted.Where(execution => execution.Attempt == attempt);
			synthetic = synthetic.Where(execution => execution.Attempt == attempt);
		}

		// At most one synthetic attempt joins the persisted page. Bound the persisted
		// branch first so its primary key supplies the order without sorting all history.
		var pageEnd = (int)Math.Min(int.MaxValue, (long)query.Skip + query.Take);
		var executions = persisted.OrderByDescending(execution => execution.Attempt)
			.Take(pageEnd).Concat(synthetic);
		var result = await executions.OrderByDescending(execution => execution.Attempt)
			.Skip(query.Skip).Take(query.Take).ToListAsync(cancellationToken);
		return [.. result.Select(static execution => new JobExecutionRecord
		{
			JobHandle = JobHandle.FromString(execution.JobHandle),
			Attempt = execution.Attempt,
			State = (JobExecutionState)execution.State,
			WorkerId = execution.WorkerId,
			AcquiredAt = execution.AcquiredAt,
			ExecutionStartedAt = execution.ExecutionStartedAt,
			CompletedAt = execution.CompletedAt,
			ExecutionTraceId = execution.ExecutionTraceId,
			ExecutionSpanId = execution.ExecutionSpanId,
			Error = execution.Error,
			IsSynthetic = execution.IsSynthetic,
		})];
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
		var page = batches.OrderByDescending(batch => batch.CreatedAt).ThenBy(batch => batch.Id)
			.Skip(query.Skip).Take(query.Take).Select(batch => batch.Id);
		var entities = await (
			from pageId in page
			join batch in Batches(connection) on pageId equals batch.Id
			orderby batch.CreatedAt descending, batch.Id
			select batch
		).ToListAsync(cancellationToken);
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

		var page = jobs.OrderBy(job => job.CreatedAt).ThenBy(job => job.Id)
			.Skip(query.Skip).Take(query.Take).Select(job => job.Id);
		return await (
			from pageId in page
			join job in JobsWithDefinitions(connection) on pageId equals job.Id
			orderby job.CreatedAt, job.Id
			select new BatchMemberStatus
			{
				JobHandle = JobHandle.FromString(job.Id),
				JobName = job.JobName,
				QueueName = job.QueueName,
				CancellationReason = job.CancellationReason,
				State = job.State,
				Attempt = job.Attempt,
				CreatedAt = job.CreatedAt,
				CompletedAt = job.CompletedAt,
				LastError = job.LastError,
			}
		).ToListAsync(cancellationToken);
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

		var rows = await (
			from batch in Batches(connection)
			where batch.Id == batchHandle.Value
			from job in Jobs(connection).Where(job => job.BatchHandle == batch.Id).DefaultIfEmpty()
			orderby job.CreatedAt, job.Id
			select new { Id = (string?)job.Id, job.JobName, job.State }
		).ToListAsync(cancellationToken);
		if (rows.Count == 0)
			return null;
		var jobs = rows.Where(static row => row.Id != null).Select(static row => new BatchGraphNode
		{
			JobHandle = JobHandle.FromString(row.Id),
			JobName = row.JobName,
			State = row.State,
		}).ToList();
		var ids = jobs.Select(static job => job.JobHandle.Value).ToList();
		var edges = ids.Count == 0
			? []
			: await Continuations(connection)
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

		await using var scope = contextScope.GetScope(out var connection);

		var rows = await (
			from job in JobsWithDefinitions(connection)
			where job.Id == jobHandle.Value
			from edge in Continuations(connection).Where(edge => edge.ChildJobHandle == job.Id).DefaultIfEmpty()
			orderby edge.ParentKind, edge.ParentId
			select new
			{
				job.Id,
				job.JobName,
				job.QueueName,
				job.CancellationReason,
				job.State,
				job.Attempt,
				job.CreatedAt,
				job.DueAt,
				job.CompletedAt,
				job.LastError,
				job.BatchHandle,
				Edge = edge,
			}
		).ToListAsync(cancellationToken);
		if (rows.Count == 0)
			return null;
		var status = rows[0];
		var edges = rows.Where(static row => row.Edge != null).Select(static row => row.Edge);
		return new JobStatus
		{
			JobHandle = JobHandle.FromString(status.Id),
			JobName = status.JobName,
			QueueName = status.QueueName,
			CancellationReason = status.CancellationReason,
			State = status.State,
			Attempt = status.Attempt,
			MaxAttempts = 0,
			CreatedAt = status.CreatedAt,
			DueAt = status.DueAt,
			CompletedAt = status.CompletedAt,
			LastError = status.LastError,
			BatchHandle = BatchHandle.FromString(status.BatchHandle),
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

	[LoggerMessage(
		EventId = LibraryEventIds.GetMonitoringDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetMonitoringDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetMonitoringDefinitionsAsync called"
	)]
	private partial void GetMonitoringDefinitionsAsyncCalled();
}

using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		GetMonitoringSnapshotAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var counts = Enum.GetValues<JobState>().ToDictionary(state => state, state => _jobs.Values.LongCount(x => x.State == state));
			var now = timeProvider.GetUtcNow();
			IReadOnlyList<JobServerSnapshot> servers = [.. _servers.Values.Where(x => x.LastHeartbeat + x.ServerTimeout >= now)];
			return new JobMonitoringSnapshot
			{
				CapturedAt = timeProvider.GetUtcNow(),
				Counts = counts,
				Recurring = [.. _recurring.Values],
				Servers = servers,
				Capabilities = this.GetCapabilities(),
			};
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
	{
		QueryJobsAsyncCalled(query);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			var jobs = _jobs.Values.AsEnumerable();
			if (query.JobHandle is { } id)
				jobs = jobs.Where(x => x.JobHandle == query.JobHandle);
			if (query.State is { } state)
				jobs = jobs.Where(x => x.State == state);
			if (!string.IsNullOrWhiteSpace(query.QueueName))
				jobs = jobs.Where(x => string.Equals(x.QueueName, query.QueueName, StringComparison.Ordinal));
			if (!string.IsNullOrWhiteSpace(query.JobName))
				jobs = jobs.Where(x => string.Equals(x.JobName, query.JobName, StringComparison.Ordinal));
			if (query.CreatedBefore is { } createdBefore)
				jobs = jobs.Where(x => x.CreatedAt < createdBefore);

			if (!string.IsNullOrWhiteSpace(query.Search))
				jobs = jobs.Where(x => x.JobName.Contains(query.Search, StringComparison.OrdinalIgnoreCase));

			return
			[
				.. jobs.OrderByDescending(x => x.CreatedAt)
					.ThenBy(x => x.JobHandle.Value, StringComparer.Ordinal)
					.Skip(Math.Max(0, query.Skip))
					.Take(Math.Clamp(query.Take, 1, 1000)),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryNonCompletedJobsAsync(string jobName, CancellationToken cancellationToken = default)
	{
		QueryNonCompletedJobsAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			return _jobs.Values
				.Where(j => string.Equals(j.JobName, jobName, StringComparison.Ordinal))
				.Where(
					j => j.State is
						JobState.AwaitingContinuation
						or JobState.WaitingForTrigger
						or JobState.Scheduled
						or JobState.Pending
						or JobState.Active
				)
				.ToList();
		}
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

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				return [];

			var executions = _executions.TryGetValue(jobHandle, out var persisted)
				? persisted.Values.AsEnumerable()
				: [];
			var synthetic = JobExecutionRecords.CreateSynthetic(job);
			if (synthetic is not null && (persisted is null || !persisted.ContainsKey(synthetic.Attempt)))
				executions = executions.Append(synthetic);
			if (query.Attempt is { } attempt)
				executions = executions.Where(execution => execution.Attempt == attempt);

			return [.. executions
				.OrderByDescending(static execution => execution.Attempt)
				.Skip(query.Skip)
				.Take(Math.Min(query.Take, 1000))];
		}
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

		lock (_gate)
		{
			if (!_batches.TryGetValue(batchHandle, out var batch))
				return null;

			return ToStatus(batch);
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		QueryBatchesAsyncCalled(query);
		await TaskScheduler.Yield();
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			var batches = _batches.Values.AsEnumerable();
			if (query.State is { } state)
				batches = batches.Where(batch => batch.State == state);
			return
			[
				.. batches.OrderByDescending(static batch => batch.CreatedAt)
					.ThenBy(static batch => batch.BatchHandle.Value, StringComparer.Ordinal)
					.Skip(query.Skip)
					.Take(query.Take)
					.Select(ToStatus),
			];
		}
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

		lock (_gate)
		{
			if (!_batches.ContainsKey(batchHandle))
				return [];

			var members = _jobs.Values.Where(job => job.BatchHandle == batchHandle);
			if (query.State is { } state)
				members = members.Where(job => job.State == state);

			return
			[
				.. members
					.OrderBy(job => job.CreatedAt)
					.ThenBy(job => job.JobHandle.Value, StringComparer.Ordinal)
					.Skip(query.Skip)
					.Take(query.Take)
					.Select(static job => new BatchMemberStatus
					{
						JobHandle = job.JobHandle,
						JobName = job.JobName,
						QueueName = job.QueueName,
						State = job.State,
						Attempt = job.Attempt,
						CreatedAt = job.CreatedAt,
						CompletedAt = job.CompletedAt,
						LastError = job.LastError,
					}),
			];
		}
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

		lock (_gate)
		{
			if (!_batches.ContainsKey(batchHandle))
				return null;

			var members = _jobs.Values
				.Where(job => job.BatchHandle == batchHandle)
				.OrderBy(job => job.CreatedAt)
				.ThenBy(job => job.JobHandle.Value, StringComparer.Ordinal)
				.ToList();

			var memberIds = members.Select(static job => job.JobHandle).ToHashSet();

			return new BatchGraph
			{
				BatchHandle = batchHandle,
				Nodes = [.. members.Select(static job => new BatchGraphNode { JobHandle = job.JobHandle, JobName = job.JobName, State = job.State })],
				Edges = [.. _edges.Where(edge => memberIds.Contains(edge.ChildJobHandle))],
			};
		}
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

		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobHandle, out var job))
				return null;

			return new JobStatus
			{
				JobHandle = job.JobHandle,
				JobName = job.JobName,
				QueueName = job.QueueName,
				State = job.State,
				Attempt = job.Attempt,
				MaxAttempts = 0,
				CreatedAt = job.CreatedAt,
				DueAt = job.DueAt,
				CompletedAt = job.CompletedAt,
				LastError = job.LastError,
				BatchHandle = job.BatchHandle,
				DependsOn = [.. _edges.Where(edge => edge.ChildJobHandle == jobHandle)],
			};
		}
	}

	private static BatchStatus ToStatus(BatchRecord batch) =>
		new()
		{
			BatchHandle = batch.BatchHandle,
			State = batch.State,
			Total = batch.TotalJobs,
			Succeeded = batch.SucceededCount,
			Failed = batch.FailedCount,
			Cancelled = batch.CancelledCount,
			Skipped = batch.SkippedCount,
			Remaining = batch.PendingCount,
			CreatedAt = batch.CreatedAt,
			StartedAt = batch.StartedAt,
			CompletedAt = batch.CompletedAt,
			FractionSettled = BatchStatus.CalculateFractionSettled(batch.TotalJobs, batch.PendingCount),
		};

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryGetMonitoringSnapshotAsyncCalled,
		EventName = "Immediate.Jobs.Shared.GetMonitoringSnapshotAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetMonitoringSnapshotAsync called"
	)]
	private partial void GetMonitoringSnapshotAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryQueryJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.QueryJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobsAsync called (Query={Query})"
	)]
	private partial void QueryJobsAsyncCalled(JobQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryQueryNonCompletedJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.QueryNonCompletedJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryNonCompletedJobsAsyncAsync called (JobName={JobName})"
	)]
	private partial void QueryNonCompletedJobsAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryQueryJobExecutionsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.QueryJobExecutionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryJobExecutionsAsync called (JobHandle={JobHandle})"
	)]
	private partial void QueryJobExecutionsAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryGetBatchStatusAsyncCalled,
		EventName = "Immediate.Jobs.Shared.GetBatchStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetBatchStatusAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void GetBatchStatusAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryQueryBatchesAsyncCalled,
		EventName = "Immediate.Jobs.Shared.QueryBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryBatchesAsync called (Query={Query})"
	)]
	private partial void QueryBatchesAsyncCalled(BatchQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryQueryBatchMembersAsyncCalled,
		EventName = "Immediate.Jobs.Shared.QueryBatchMembersAsyncCalled",
		Level = LogLevel.Debug,
		Message = "QueryBatchMembersAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void QueryBatchMembersAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryGetBatchGraphAsyncCalled,
		EventName = "Immediate.Jobs.Shared.GetBatchGraphAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetBatchGraphAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void GetBatchGraphAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryGetJobStatusAsyncCalled,
		EventName = "Immediate.Jobs.Shared.GetJobStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobStatusAsync called (JobHandle={JobHandle})"
	)]
	private partial void GetJobStatusAsyncCalled(JobHandle jobHandle);
}

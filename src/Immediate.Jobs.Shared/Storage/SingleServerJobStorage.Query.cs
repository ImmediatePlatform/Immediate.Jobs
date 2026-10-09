using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	/// <inheritdoc />
	public async ValueTask<JobMonitoringDefinitions> GetMonitoringDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		SingleServerGetMonitoringDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await DurableStorage.GetMonitoringDefinitionsAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobMonitoringSnapshot> GetMonitoringSnapshotAsync(CancellationToken cancellationToken = default)
	{
		SingleServerGetMonitoringSnapshotAsyncCalled();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		var snapshot = await DurableStorage.GetMonitoringSnapshotAsync(cancellationToken);
		return snapshot with { Servers = PrimaryStorage.GetLiveServersSnapshot(), Capabilities = this.GetCapabilities() };
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryJobsAsync(
		JobQuery query,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerQueryJobsAsyncCalled(query);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.QueryJobsAsync(query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobRecord>> QueryNonCompletedJobsAsync(
		string jobName,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerQueryNonCompletedJobsAsyncCalled(jobName);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.QueryNonCompletedJobsAsync(jobName, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobExecutionRecord>> QueryJobExecutionsAsync(
		JobHandle jobHandle,
		JobExecutionQuery query,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerQueryJobExecutionsAsyncCalled(jobHandle, query);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		// Recovery restores current jobs and related state into the primary, but not retained executions.
		return await DurableStorage.QueryJobExecutionsAsync(jobHandle, query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<BatchStatus?> GetBatchStatusAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerGetBatchStatusAsyncCalled(batchHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetBatchStatusAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchStatus>> QueryBatchesAsync(
		BatchQuery query,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerQueryBatchesAsyncCalled(query);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.QueryBatchesAsync(query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<BatchMemberStatus>> QueryBatchMembersAsync(
		BatchHandle batchHandle,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerQueryBatchMembersAsyncCalled(batchHandle, query);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.QueryBatchMembersAsync(batchHandle, query, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<BatchGraph?> GetBatchGraphAsync(
		BatchHandle batchHandle,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerGetBatchGraphAsyncCalled(batchHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetBatchGraphAsync(batchHandle, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobStatusAsync(
		JobHandle jobHandle,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerGetJobStatusAsyncCalled(jobHandle);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetJobStatusAsync(jobHandle, cancellationToken);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerGetMonitoringSnapshotAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerGetMonitoringSnapshotAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage GetMonitoringSnapshotAsync called"
	)]
	private partial void SingleServerGetMonitoringSnapshotAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerQueryJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerQueryJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage QueryJobsAsync called (Query={Query})"
	)]
	private partial void SingleServerQueryJobsAsyncCalled(JobQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerQueryNonCompletedJobsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerQueryNonCompletedJobsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage QueryNonCompletedJobsAsync called (JobName={JobName})"
	)]
	private partial void SingleServerQueryNonCompletedJobsAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerQueryJobExecutionsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerQueryJobExecutionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage QueryJobExecutionsAsync called (JobHandle={JobHandle}, Query={Query})"
	)]
	private partial void SingleServerQueryJobExecutionsAsyncCalled(JobHandle jobHandle, JobExecutionQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerGetBatchStatusAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerGetBatchStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage GetBatchStatusAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void SingleServerGetBatchStatusAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerQueryBatchesAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerQueryBatchesAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage QueryBatchesAsync called (Query={Query})"
	)]
	private partial void SingleServerQueryBatchesAsyncCalled(BatchQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerQueryBatchMembersAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerQueryBatchMembersAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage QueryBatchMembersAsync called (BatchHandle={BatchHandle}, Query={Query})"
	)]
	private partial void SingleServerQueryBatchMembersAsyncCalled(BatchHandle batchHandle, BatchMemberQuery query);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerGetBatchGraphAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerGetBatchGraphAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage GetBatchGraphAsync called (BatchHandle={BatchHandle})"
	)]
	private partial void SingleServerGetBatchGraphAsyncCalled(BatchHandle batchHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerGetJobStatusAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerGetJobStatusAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage GetJobStatusAsync called (JobHandle={JobHandle})"
	)]
	private partial void SingleServerGetJobStatusAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerGetMonitoringDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerGetMonitoringDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetMonitoringDefinitionsAsync called"
	)]
	private partial void SingleServerGetMonitoringDefinitionsAsyncCalled();
}

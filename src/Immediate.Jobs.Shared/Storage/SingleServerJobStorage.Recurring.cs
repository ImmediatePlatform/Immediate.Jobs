using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	private readonly SemaphoreSlim _recurringMaterialization = new(1, 1);

	/// <inheritdoc />
	public async ValueTask MergeRecurringSchedulesListAsync(
		IReadOnlyList<RecurringJobSchedule> schedules,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerMergeRecurringSchedulesListAsyncCalled(schedules.Count);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.MergeRecurringSchedulesListAsync(schedules, cancellationToken);
		await PrimaryStorage.MergeRecurringSchedulesListAsync(schedules, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		SingleServerUpsertRecurringAsyncCalled(schedule.Name);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.UpsertRecurringAsync(schedule, cancellationToken);
		await PrimaryStorage.UpsertRecurringAsync(schedule, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		SingleServerRemoveRecurringAsyncCalled(name);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.RemoveRecurringAsync(name, cancellationToken);
		await PrimaryStorage.RemoveRecurringAsync(name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		SingleServerPauseRecurringAsyncCalled(name);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.PauseRecurringAsync(name, cancellationToken);
		await PrimaryStorage.PauseRecurringAsync(name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		SingleServerResumeRecurringAsyncCalled(name);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.ResumeRecurringAsync(name, cancellationToken);
		await PrimaryStorage.ResumeRecurringAsync(name, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerGetDueRecurringAsyncCalled(now, batchSize);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetDueRecurringAsync(now, batchSize, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(DateTimeOffset now, int batchSize, IReadOnlyList<string> jobNames, CancellationToken cancellationToken = default)
	{
		SingleServerGetDueRecurringAsyncCalled(now, batchSize);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await PrimaryStorage.GetDueRecurringAsync(now, batchSize, jobNames, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		IReadOnlyList<JobContinuationEdge>? dependencies = null,
		CancellationToken cancellationToken = default
	)
	{
		SingleServerMaterializeRecurringAsyncCalled(schedule.Name, job.JobHandle, nextRunAt);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);

		await _recurringMaterialization.WaitAsync(cancellationToken);

		try
		{
			var durableResult = await DurableStorage
				.MaterializeRecurringAsync(schedule, job, nextRunAt, dependencies, cancellationToken);

			var primaryResult = await PrimaryStorage
				.MaterializeRecurringAsync(schedule, job, nextRunAt, dependencies, cancellationToken);

			if (primaryResult != durableResult)
			{
				throw new ImmediateJobException(
					"The durable recurring-job replica has drifted from the authoritative in-memory schedule."
				);
			}

			return primaryResult;
		}
		finally
		{
			_recurringMaterialization.Release();
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerMergeRecurringSchedulesListAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerMergeRecurringSchedulesListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage MergeRecurringSchedulesListAsync called (Schedules={Schedules})"
	)]
	private partial void SingleServerMergeRecurringSchedulesListAsyncCalled(int schedules);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerUpsertRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerUpsertRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage UpsertRecurringAsync called (ScheduleName={ScheduleName})"
	)]
	private partial void SingleServerUpsertRecurringAsyncCalled(string scheduleName);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerRemoveRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerRemoveRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage RemoveRecurringAsync called (ScheduleName={ScheduleName})"
	)]
	private partial void SingleServerRemoveRecurringAsyncCalled(string scheduleName);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerPauseRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerPauseRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage PauseRecurringAsync called (ScheduleName={ScheduleName})"
	)]
	private partial void SingleServerPauseRecurringAsyncCalled(string scheduleName);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerResumeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerResumeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage ResumeRecurringAsync called (ScheduleName={ScheduleName})"
	)]
	private partial void SingleServerResumeRecurringAsyncCalled(string scheduleName);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerGetDueRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerGetDueRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage GetDueRecurringAsync called (Now={Now}, BatchSize={BatchSize})"
	)]
	private partial void SingleServerGetDueRecurringAsyncCalled(DateTimeOffset now, int batchSize);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerMaterializeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerMaterializeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage MaterializeRecurringAsync called (ScheduleName={ScheduleName}, JobHandle={JobHandle}, NextRunAt={NextRunAt})"
	)]
	private partial void SingleServerMaterializeRecurringAsyncCalled(string scheduleName, JobHandle jobHandle, DateTimeOffset nextRunAt);
}

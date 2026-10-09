using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	/// <inheritdoc />
	public async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
	{
		SingleServerMergeJobDefinitionsListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);
		try
		{
			await DurableStorage.MergeJobDefinitionsListAsync(registration, cancellationToken);
			// Refresh the entire durable catalogue of schedules, from durable storage.
			var snapshot = await DurableStorage.GetMonitoringSnapshotAsync(cancellationToken);
			await PrimaryStorage.MergeRecurringSchedulesListAsync(
				snapshot.Recurring.Where(static schedule => schedule.IsCodeDefined).ToList(), cancellationToken);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		SingleServerGetJobDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await DurableStorage.GetJobDefinitionsAsync(cancellationToken);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerMergeJobDefinitionsListAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerMergeJobDefinitionsListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SingleServerMergeJobDefinitionsListAsyncCalled"
	)]
	private partial void SingleServerMergeJobDefinitionsListAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerGetJobDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerGetJobDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "SingleServerGetJobDefinitionsAsyncCalled"
	)]
	private partial void SingleServerGetJobDefinitionsAsyncCalled();
}

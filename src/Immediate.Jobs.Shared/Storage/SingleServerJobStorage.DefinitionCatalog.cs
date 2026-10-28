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
			await PrimaryStorage.MergeJobDefinitionsListAsync(registration, cancellationToken);
			_acquisitionDefinitionsToRefresh.IntersectWith(registration.Definitions.Select(static definition => definition.Name));
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

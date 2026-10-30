using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	/// <inheritdoc />
	public async ValueTask PauseJobAsync(string jobName, CancellationToken cancellationToken = default)
	{
		SingleServerPauseJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);
		try
		{
			await DurableStorage.PauseJobAsync(jobName, cancellationToken);
			await PrimaryStorage.PauseJobAsync(jobName, CancellationToken.None);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask ResumeJobAsync(string jobName, CancellationToken cancellationToken = default)
	{
		SingleServerResumeJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await _writeThrough.WaitAsync(cancellationToken);
		try
		{
			await DurableStorage.ResumeJobAsync(jobName, cancellationToken);
			await PrimaryStorage.ResumeJobAsync(jobName, CancellationToken.None);
		}
		finally
		{
			_writeThrough.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, CancellationToken cancellationToken = default)
	{
		SingleServerGetJobAcquisitionStateAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await DurableStorage.GetJobAcquisitionStateAsync(jobName, cancellationToken);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerPauseJobAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerPauseJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseJobAsync called (JobName={JobName})"
	)]
	private partial void SingleServerPauseJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerResumeJobAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerResumeJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeJobAsync called (JobName={JobName})"
	)]
	private partial void SingleServerResumeJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerGetJobAcquisitionStateAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerGetJobAcquisitionStateAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobAcquisitionStateAsync called (JobName={JobName})"
	)]
	private partial void SingleServerGetJobAcquisitionStateAsyncCalled(string jobName);
}

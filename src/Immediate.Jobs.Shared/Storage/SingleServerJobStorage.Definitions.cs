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
		await DurableStorage.PauseJobAsync(jobName, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeJobAsync(string jobName, JobAcquisitionLimits limits, CancellationToken cancellationToken = default)
	{
		SingleServerResumeJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await DurableStorage.ResumeJobAsync(jobName, limits, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, JobAcquisitionLimits limits, CancellationToken cancellationToken = default)
	{
		SingleServerGetJobAcquisitionStateAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await DurableStorage.GetJobAcquisitionStateAsync(jobName, limits, cancellationToken);
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

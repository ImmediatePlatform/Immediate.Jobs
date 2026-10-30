using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

internal sealed partial class SingleServerJobStorage
{
	private const int RecoveryBatchSize = 1000;

	private readonly TaskCompletionSource _initializationTask = new();

	private bool _initialized;

	/// <inheritdoc />
	public async ValueTask InitializeAsync(bool interruptActiveLeases, CancellationToken cancellationToken = default)
	{
		SingleServerInitializeAsyncCalled();
		await TaskScheduler.Yield();
		// Initialization runs once during application startup; callers must not invoke it concurrently.
		if (_initialized)
			return;

		try
		{
			await DurableStorage.InitializeAsync(interruptActiveLeases: true, cancellationToken);
			var monitoringDefinitions = await DurableStorage.GetMonitoringDefinitionsAsync(cancellationToken);
			var snapshot = await DurableStorage.GetMonitoringSnapshotAsync(cancellationToken);
			PrimaryStorage.LoadPersistedJobDefinitions(monitoringDefinitions.Jobs);
			var statuses = snapshot.Recurring.ToDictionary(static status => status.Name, StringComparer.OrdinalIgnoreCase);
			var schedules = monitoringDefinitions.Recurring
				.Select(definition => RecurringJobSchedule.FromDefinition(definition, statuses[definition.Name])).ToList();

			var recoveredJobs = new List<JobRecord>();
			var recoveredEdges = new List<JobContinuationEdge>();
			for (var skip = 0; ; skip += RecoveryBatchSize)
			{
				var jobs = await DurableStorage.QueryJobsAsync(new() { Skip = skip, Take = RecoveryBatchSize }, cancellationToken);
				recoveredJobs.AddRange(jobs);
				if (jobs.Count != 0)
				{
					var incomingEdges = await JobGraphStorage.GetIncomingEdgesAsync(jobs.Select(static job => job.JobHandle).ToList(), cancellationToken);
					recoveredEdges.AddRange(incomingEdges);
				}

				if (jobs.Count < RecoveryBatchSize)
					break;
			}

			var recoveredBatches = new List<BatchRecord>();
			for (var skip = 0; ; skip += RecoveryBatchSize)
			{
				var batches = await JobGraphStorage.QueryBatchesAsync(new() { Skip = skip, Take = RecoveryBatchSize }, cancellationToken);
				foreach (var status in batches)
				{
					recoveredBatches.Add(new()
					{
						BatchHandle = status.BatchHandle,
						CreatedAt = status.CreatedAt,
						TotalJobs = status.Total,
						PendingCount = status.Remaining,
						SucceededCount = status.Succeeded,
						FailedCount = status.Failed,
						CancelledCount = status.Cancelled,
						SkippedCount = status.Skipped,
						StartedAt = status.StartedAt,
						CompletedAt = status.CompletedAt,
						State = status.State,
					});
				}

				if (batches.Count < RecoveryBatchSize)
					break;
			}

			cancellationToken.ThrowIfCancellationRequested();
			PrimaryStorage.LoadPersistedJobState(recoveredJobs, recoveredBatches, recoveredEdges, schedules);
			PrimaryStorage.ApplyDefinitionAcquisitionStates(snapshot.DefinitionStatuses);
			_initializationTask.SetResult();
			_initialized = true;
		}
		catch (Exception ex)
		{
			_initializationTask.SetException(ex);
			_initialized = true;
			throw;
		}
	}

	private async Task EnsureInitializedAsync(CancellationToken token)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _initializationTask.Task.WaitAsync(token);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerInitializeAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerInitializeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage InitializeAsync called"
	)]
	private partial void SingleServerInitializeAsyncCalled();
}

using Microsoft.Extensions.Logging;
using System.Data.Common;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	private ITable<ImmediateJobDefinitionEntity> DefinitionStates(DataConnection connection) => WithSchema(connection.GetTable<ImmediateJobDefinitionEntity>());
	private ITable<ImmediateJobAcquisitionEntity> Acquisitions(DataConnection connection) => WithSchema(connection.GetTable<ImmediateJobAcquisitionEntity>());

	/// <inheritdoc />
	public async ValueTask PauseJobAsync(string jobName, CancellationToken cancellationToken = default)
	{
		PauseJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await UpdateDefinitionAsync(jobName, paused: true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeJobAsync(string jobName, CancellationToken cancellationToken = default)
	{
		ResumeJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await UpdateDefinitionAsync(jobName, paused: false, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, CancellationToken cancellationToken = default)
	{
		GetJobAcquisitionStateAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await using var scope = contextScope.GetScope(out var connection);
		var states = await ReadDefinitionStatusesAsync(connection,
			Definitions(connection).Where(item => item.Name == jobName), timeProvider.GetUtcNow(), cancellationToken);
		return states.SingleOrDefault() ?? throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");
	}

	private async Task<ImmediateJobDefinitionMetadataEntity> GetStoredDefinitionAsync(DataConnection connection, string jobName, CancellationToken cancellationToken) =>
		await Definitions(connection).SingleOrDefaultAsync(definition => definition.Name == jobName, cancellationToken)
			?? throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");

	private async ValueTask UpdateDefinitionAsync(string jobName, bool paused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await using var scope = contextScope.GetScope(out var connection);
		var metadata = await GetStoredDefinitionAsync(connection, jobName, cancellationToken);
		if (paused)
		{
			if (await PauseDefinitionAsync(connection, metadata.Name, cancellationToken) == 0)
			{
				await EnsureDefinitionAsync(metadata.Name, cancellationToken);
				await PauseDefinitionAsync(connection, metadata.Name, cancellationToken);
			}

			return;
		}

		await EnsureDefinitionAsync(metadata.Name, cancellationToken);
		await connection.BeginTransactionAsync(cancellationToken);
		var definition = await LockDefinitionAsync(connection, metadata.Name, cancellationToken);
		definition.IsPaused = false;
		await EvaluateDefinitionAsync(connection, definition, ToRecord(metadata).AcquisitionLimits, timeProvider.GetUtcNow(), cancellationToken);

		await SaveDefinitionAsync(connection, definition, cancellationToken);
		await connection.CommitTransactionAsync(cancellationToken);
	}

	private Task<int> PauseDefinitionAsync(DataConnection connection, string jobName, CancellationToken cancellationToken) =>
		DefinitionStates(connection).Where(item => item.JobName == jobName)
			.Set(item => item.IsPaused, value: true)
			.Set(item => item.AcquisitionStatus, JobAcquisitionStatus.Paused)
			.Set(item => item.NextEligibleAt, (DateTimeOffset?)null)
			.Set(item => item.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken);

	private async ValueTask EnsureDefinitionsAsync(IEnumerable<string> jobNames, CancellationToken cancellationToken)
	{
		var names = jobNames.Distinct(StringComparer.Ordinal).ToList();
		if (names.Count == 0)
			return;
		await using var scope = contextScope.GetScope(out var connection);
		var existing = await DefinitionStates(connection).Where(item => names.Contains(item.JobName))
			.Select(item => item.JobName).ToListAsync(cancellationToken);
		foreach (var name in names.Except(existing, StringComparer.Ordinal))
		{
			try
			{
				await InsertAsync(connection, new ImmediateJobDefinitionEntity { JobName = name, ConcurrencyStamp = Guid.NewGuid() }, cancellationToken);
			}
			catch (DbException)
			{
				if (!await DefinitionStates(connection).AnyAsync(item => item.JobName == name, cancellationToken))
					throw;
			}
		}
	}

	private async ValueTask EnsureDefinitionAsync(string jobName, CancellationToken cancellationToken)
	{
		await using var scope = contextScope.GetScope(out var connection);
		if (await DefinitionStates(connection).AnyAsync(item => item.JobName == jobName, cancellationToken))
			return;
		try
		{
			await InsertAsync(connection, new ImmediateJobDefinitionEntity { JobName = jobName, ConcurrencyStamp = Guid.NewGuid() }, cancellationToken);
		}

		catch (DbException)
		{
			if (!await DefinitionStates(connection).AnyAsync(item => item.JobName == jobName, cancellationToken))
				throw;
		}
	}

	private async Task<ImmediateJobDefinitionEntity> LockDefinitionAsync(DataConnection connection, string jobName, CancellationToken cancellationToken)
	{
		// Serialize acquisition and pause on the definition before querying capacity.
		await DefinitionStates(connection).Where(item => item.JobName == jobName)
			.Set(item => item.ConcurrencyStamp, Guid.NewGuid()).UpdateAsync(cancellationToken);
		return await DefinitionStates(connection).SingleAsync(item => item.JobName == jobName, cancellationToken);
	}

	private async Task<JobAcquisitionState> EvaluateDefinitionAsync(DataConnection connection, ImmediateJobDefinitionEntity definition, JobAcquisitionLimits limits, DateTimeOffset now, CancellationToken cancellationToken, bool includeActiveCount = false)
	{
		// UTC ticks retain TimeSpan precision on providers whose native timestamp precision is lower.
		var cutoff = limits.SlidingWindowPeriod is { } period ? now - period : DateTimeOffset.MinValue;
		var acquisitions = limits.SlidingWindowMax > 0
			? await Acquisitions(connection).Where(item => item.JobName == definition.JobName && item.AcquiredAt > cutoff)
				.OrderBy(item => item.AcquiredAt).Select(item => item.AcquiredAt).ToListAsync(cancellationToken)
			: [];
		var activeCount = includeActiveCount || limits.MaxConcurrency > 0
			? await Jobs(connection).CountAsync(job => job.JobName == definition.JobName && job.State == JobState.Active && job.LeaseExpiresAt > now, cancellationToken)
			: 0;
		var state = JobAcquisitionEvaluator.Evaluate(definition.JobName, definition.IsPaused, limits, now,
			acquisitions,
			definition.FixedWindowStart, definition.FixedWindowCount, activeCount);
		definition.AcquisitionStatus = state.AcquisitionStatus;
		definition.NextEligibleAt = state.NextEligibleAt;
		return state;
	}

	private async Task SaveDefinitionAsync(DataConnection connection, ImmediateJobDefinitionEntity definition, CancellationToken cancellationToken)
	{
		await DefinitionStates(connection).Where(item => item.JobName == definition.JobName)
			.Set(item => item.IsPaused, definition.IsPaused)
			.Set(item => item.AcquisitionStatus, definition.AcquisitionStatus)
			.Set(item => item.NextEligibleAt, definition.NextEligibleAt)
			.Set(item => item.FixedWindowStart, definition.FixedWindowStart)
			.Set(item => item.FixedWindowCount, definition.FixedWindowCount)
			.UpdateAsync(cancellationToken);
	}

	private async ValueTask<(ImmediateJobDefinitionEntity Definition, JobAcquisitionLimits Limits)?> ReserveDefinitionAsync(DataConnection connection, string jobName, DateTimeOffset now, CancellationToken cancellationToken)
	{
		var definition = await LockDefinitionAsync(connection, jobName, cancellationToken);
		var metadata = await Definitions(connection).SingleOrDefaultAsync(item => item.Name == jobName, cancellationToken);
		if (metadata is null)
			return null;

		var limits = ToRecord(metadata).AcquisitionLimits;
		var state = await EvaluateDefinitionAsync(connection, definition, limits, now, cancellationToken);
		if (state.AcquisitionStatus != JobAcquisitionStatus.Ready)
		{
			await SaveDefinitionAsync(connection, definition, cancellationToken);
			return null;
		}

		if (limits.SlidingWindowPeriod is { } period)
		{
			var cutoff = now - period;
			await Acquisitions(connection).Where(item => item.JobName == jobName && item.AcquiredAt <= cutoff).DeleteAsync(cancellationToken);
			await InsertAsync(connection, new ImmediateJobAcquisitionEntity { Id = Guid.NewGuid(), JobName = jobName, AcquiredAt = now }, cancellationToken);
		}

		if (limits.FixedWindowStart(now) is { } start)
		{
			if (definition.FixedWindowStart != start)
			{
				definition.FixedWindowStart = start;
				definition.FixedWindowCount = 0;
			}

			definition.FixedWindowCount++;
		}

		return (definition, limits);
	}

	private async Task RefreshAcquiredDefinitionAsync(DataConnection connection, ImmediateJobDefinitionEntity definition, JobAcquisitionLimits limits, DateTimeOffset now, CancellationToken cancellationToken)
	{
		await EvaluateDefinitionAsync(connection, definition, limits, now, cancellationToken);
		await SaveDefinitionAsync(connection, definition, cancellationToken);
	}

	private async ValueTask<List<string>> FilterAcquisitionNamesAsync(Dictionary<string, int> capacities, CancellationToken cancellationToken)
	{
		var candidates = capacities.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToList();
		if (candidates.Count == 0)
			return [];

		await using var scope = contextScope.GetScope(out var connection);
		// Advisory only: claims lock and recheck the durable definition.
		var states = await ReadDefinitionStatusesAsync(connection,
			Definitions(connection).Where(item => candidates.Contains(item.Name)),
			timeProvider.GetUtcNow(), cancellationToken, includeActiveCount: false);
		return states.Where(static state => state.AcquisitionStatus == JobAcquisitionStatus.Ready)
			.Select(static state => state.JobName).ToList();
	}

	[LoggerMessage(
		EventId = LibraryEventIds.PauseJobAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.PauseJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseJobAsync called (JobName={JobName})"
	)]
	private partial void PauseJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.ResumeJobAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.ResumeJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeJobAsync called (JobName={JobName})"
	)]
	private partial void ResumeJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobAcquisitionStateAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetJobAcquisitionStateAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobAcquisitionStateAsync called (JobName={JobName})"
	)]
	private partial void GetJobAcquisitionStateAsyncCalled(string jobName);
}

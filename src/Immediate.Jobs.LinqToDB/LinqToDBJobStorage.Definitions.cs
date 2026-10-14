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
		var metadata = await GetStoredDefinitionAsync(connection, jobName, cancellationToken);
		var definition = await DefinitionStates(connection).SingleOrDefaultAsync(item => item.JobName == metadata.Name, cancellationToken)
			?? new() { JobName = metadata.Name };
		return await EvaluateDefinitionAsync(connection, definition, ToRecord(metadata).AcquisitionLimits, timeProvider.GetUtcNow(), cancellationToken, includeActiveCount: true);
	}

	private async Task<ImmediateJobDefinitionMetadataEntity> GetStoredDefinitionAsync(DataConnection connection, string jobName, CancellationToken cancellationToken)
	{
		IQueryable<ImmediateJobDefinitionMetadataEntity> query = Definitions(connection);
		// SQLite's NOCASE collation only handles ASCII; compare Unicode aliases in the client.
		if (!connection.DataProvider.Name.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
			query = query.Where(definition => definition.Name == jobName);

		var definitions = await query.ToListAsync(cancellationToken);
		return definitions.SingleOrDefault(definition => string.Equals(definition.Name, jobName, StringComparison.OrdinalIgnoreCase))
			?? throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");
	}

	private async ValueTask UpdateDefinitionAsync(string jobName, bool paused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await using var scope = contextScope.GetScope(out var connection);
		var metadata = await GetStoredDefinitionAsync(connection, jobName, cancellationToken);
		await EnsureDefinitionAsync(metadata.Name, cancellationToken);
		await connection.BeginTransactionAsync(cancellationToken);
		var definition = await LockDefinitionAsync(connection, metadata.Name, cancellationToken);
		definition.IsPaused = paused;
		if (paused)
		{
			definition.AcquisitionStatus = JobAcquisitionStatus.Paused;
			definition.NextEligibleAt = null;
		}
		else
		{
			await EvaluateDefinitionAsync(connection, definition, ToRecord(metadata).AcquisitionLimits, timeProvider.GetUtcNow(), cancellationToken);
		}

		await SaveDefinitionAsync(connection, definition, cancellationToken);
		await connection.CommitTransactionAsync(cancellationToken);
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

	private async ValueTask<JobAcquisitionLimits?> ReserveDefinitionAsync(DataConnection connection, string jobName, DateTimeOffset now, CancellationToken cancellationToken)
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

		await SaveDefinitionAsync(connection, definition, cancellationToken);
		return limits;
	}

	private async Task RefreshAcquiredDefinitionAsync(DataConnection connection, string jobName, JobAcquisitionLimits limits, DateTimeOffset now, CancellationToken cancellationToken)
	{
		var definition = await DefinitionStates(connection).SingleAsync(item => item.JobName == jobName, cancellationToken);
		await EvaluateDefinitionAsync(connection, definition, limits, now, cancellationToken);
		await SaveDefinitionAsync(connection, definition, cancellationToken);
	}

	private async ValueTask<List<string>> FilterAcquisitionNamesAsync(Dictionary<string, int> capacities, CancellationToken cancellationToken)
	{
		var candidates = capacities.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToList();
		if (candidates.Count == 0)
			return [];

		await using var scope = contextScope.GetScope(out var connection);
		// This is an advisory read. The definition row is locked and rechecked when claiming a job.
		IQueryable<ImmediateJobDefinitionMetadataEntity> query = Definitions(connection);
		if (!connection.DataProvider.Name.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
			query = query.Where(item => candidates.Contains(item.Name));

		var metadata = (await query.ToListAsync(cancellationToken))
			.Where(item => capacities.TryGetValue(item.Name, out var capacity) && capacity > 0).ToList();
		if (metadata.Count == 0)
			return [];

		var canonicalNames = metadata.Select(static item => item.Name).ToList();
		// Resolve names before reading counters: metadata and counter columns may have different collations.
		var definitions = await DefinitionStates(connection).Where(item => canonicalNames.Contains(item.JobName))
			.ToDictionaryAsync(static item => item.JobName, StringComparer.Ordinal, cancellationToken);
		var names = new List<string>();
		var now = timeProvider.GetUtcNow();
		foreach (var item in metadata)
		{
			var definition = definitions.GetValueOrDefault(item.Name) ?? new() { JobName = item.Name };
			var state = await EvaluateDefinitionAsync(connection, definition, ToRecord(item).AcquisitionLimits, now, cancellationToken);
			if (state.AcquisitionStatus == JobAcquisitionStatus.Ready)
				names.Add(item.Name);
		}

		return names;
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

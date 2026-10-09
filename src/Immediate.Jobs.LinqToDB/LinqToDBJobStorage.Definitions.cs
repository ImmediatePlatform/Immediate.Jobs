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
		await UpdateDefinitionAsync(jobName, new(), paused: true, cancellationToken);
	}
	/// <inheritdoc />
	public async ValueTask ResumeJobAsync(string jobName, JobAcquisitionLimits limits, CancellationToken cancellationToken = default)
	{
		ResumeJobAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await UpdateDefinitionAsync(jobName, limits, paused: false, cancellationToken);
	}
	/// <inheritdoc />
	public async ValueTask<JobAcquisitionState> GetJobAcquisitionStateAsync(string jobName, JobAcquisitionLimits limits, CancellationToken cancellationToken = default)
	{
		GetJobAcquisitionStateAsyncCalled(jobName);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		limits.Validate();
		await using var scope = contextScope.GetScope(out var connection);
		var definition = await DefinitionStates(connection).SingleOrDefaultAsync(item => item.JobName == jobName, cancellationToken)
			?? new() { JobName = jobName };
		return await EvaluateDefinitionAsync(connection, definition, limits, timeProvider.GetUtcNow(), cancellationToken, includeActiveCount: true);
	}

	private async ValueTask UpdateDefinitionAsync(string jobName, JobAcquisitionLimits limits, bool paused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		limits.Validate();
		await EnsureDefinitionAsync(jobName, cancellationToken);
		await using var scope = contextScope.GetScope(out var connection);
		_ = await connection.BeginTransactionAsync(cancellationToken);
		var definition = await LockDefinitionAsync(connection, jobName, cancellationToken);
		definition.IsPaused = paused;
		_ = await EvaluateDefinitionAsync(connection, definition, limits, timeProvider.GetUtcNow(), cancellationToken);
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
			_ = await InsertAsync(connection, new ImmediateJobDefinitionEntity { JobName = jobName, ConcurrencyStamp = Guid.NewGuid() }, cancellationToken);
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
		_ = await DefinitionStates(connection).Where(item => item.JobName == jobName)
			.Set(item => item.ConcurrencyStamp, Guid.NewGuid()).UpdateAsync(cancellationToken);
		return await DefinitionStates(connection).SingleAsync(item => item.JobName == jobName, cancellationToken);
	}

	private async Task<JobAcquisitionState> EvaluateDefinitionAsync(DataConnection connection, ImmediateJobDefinitionEntity definition, JobAcquisitionLimits limits, DateTimeOffset now, CancellationToken cancellationToken, bool includeActiveCount = false)
	{
		// UTC ticks retain TimeSpan precision on providers whose native timestamp precision is lower.
		var cutoff = limits.SlidingWindowPeriod is { } period ? (now - period).UtcTicks : 0;
		var acquisitions = limits.SlidingWindowMax > 0
			? await Acquisitions(connection).Where(item => item.JobName == definition.JobName && item.AcquiredAt > cutoff)
				.OrderBy(item => item.AcquiredAt).Select(item => item.AcquiredAt).ToListAsync(cancellationToken)
			: [];
		var activeCount = includeActiveCount || limits.MaxConcurrency > 0
			? await Jobs(connection).CountAsync(job => job.JobName == definition.JobName && job.State == JobState.Active && job.LeaseExpiresAt > now, cancellationToken)
			: 0;
		var state = JobAcquisitionEvaluator.Evaluate(definition.JobName, definition.IsPaused, limits, now,
			acquisitions.Select(static ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).ToList(),
			definition.FixedWindowStart is { } windowTicks ? new DateTimeOffset(windowTicks, TimeSpan.Zero) : null, definition.FixedWindowCount, activeCount);
		definition.AcquisitionStatus = state.AcquisitionStatus;
		definition.NextEligibleAt = state.NextEligibleAt?.UtcTicks;
		return state;
	}

	private async Task SaveDefinitionAsync(DataConnection connection, ImmediateJobDefinitionEntity definition, CancellationToken cancellationToken)
	{
		_ = await DefinitionStates(connection).Where(item => item.JobName == definition.JobName)
			.Set(item => item.IsPaused, definition.IsPaused)
			.Set(item => item.AcquisitionStatus, definition.AcquisitionStatus)
			.Set(item => item.NextEligibleAt, definition.NextEligibleAt)
			.Set(item => item.FixedWindowStart, definition.FixedWindowStart)
			.Set(item => item.FixedWindowCount, definition.FixedWindowCount)
			.UpdateAsync(cancellationToken);
	}

	private async ValueTask<bool> ReserveDefinitionAsync(DataConnection connection, string jobName, JobAcquisitionRequest? request, DateTimeOffset now, CancellationToken cancellationToken)
	{
		var limits = request?.LimitsFor(jobName) ?? new();
		var definition = await LockDefinitionAsync(connection, jobName, cancellationToken);
		var state = await EvaluateDefinitionAsync(connection, definition, limits, now, cancellationToken);
		if (state.AcquisitionStatus != JobAcquisitionStatus.Ready)
		{
			await SaveDefinitionAsync(connection, definition, cancellationToken);
			return false;
		}

		if (limits.SlidingWindowPeriod is { } period)
		{
			var cutoff = (now - period).UtcTicks;
			_ = await Acquisitions(connection).Where(item => item.JobName == jobName && item.AcquiredAt <= cutoff).DeleteAsync(cancellationToken);
			_ = await InsertAsync(connection, new ImmediateJobAcquisitionEntity { Id = Guid.NewGuid(), JobName = jobName, AcquiredAt = now.UtcTicks }, cancellationToken);
		}

		if (limits.FixedWindowStart(now) is { } start)
		{
			if (definition.FixedWindowStart != start.UtcTicks)
			{
				definition.FixedWindowStart = start.UtcTicks;
				definition.FixedWindowCount = 0;
			}

			definition.FixedWindowCount++;
		}

		await SaveDefinitionAsync(connection, definition, cancellationToken);
		return true;
	}

	private async Task RefreshAcquiredDefinitionAsync(DataConnection connection, string jobName, JobAcquisitionRequest? request, DateTimeOffset now, CancellationToken cancellationToken)
	{
		var definition = await DefinitionStates(connection).SingleAsync(item => item.JobName == jobName, cancellationToken);
		_ = await EvaluateDefinitionAsync(connection, definition, request?.LimitsFor(jobName) ?? new(), now, cancellationToken);
		await SaveDefinitionAsync(connection, definition, cancellationToken);
	}

	private async ValueTask<List<string>> FilterAcquisitionNamesAsync(Dictionary<string, int> capacities, JobAcquisitionRequest request, CancellationToken cancellationToken)
	{
		var candidates = capacities.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToList();
		if (candidates.Count == 0)
			return [];

		await using var scope = contextScope.GetScope(out var connection);
		// This is an advisory read. The definition row is locked and rechecked when claiming a job.
		var stored = await DefinitionStates(connection).Where(item => candidates.Contains(item.JobName)).ToListAsync(cancellationToken);
		var definitions = stored.ToDictionary(static item => item.JobName, StringComparer.Ordinal);
		var names = new List<string>();
		var now = timeProvider.GetUtcNow();
		foreach (var name in candidates)
		{
			var definition = definitions.GetValueOrDefault(name) ?? new() { JobName = name };
			var state = await EvaluateDefinitionAsync(connection, definition, request.LimitsFor(name), now, cancellationToken);
			if (state.AcquisitionStatus == JobAcquisitionStatus.Ready)
				names.Add(name);
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

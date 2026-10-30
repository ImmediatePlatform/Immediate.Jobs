using Microsoft.Extensions.Logging;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
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
		return await ReadWithStrategyAsync(async (context, token) =>
		{
			var states = await ReadDefinitionStatusesAsync(context,
				context.Set<ImmediateJobDefinitionMetadataEntity>().Where(item => item.Name == jobName),
				_timeProvider.GetUtcNow(), token);
			return states.SingleOrDefault() ?? throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");
		}, cancellationToken);
	}

	private static async Task<ImmediateJobDefinitionMetadataEntity> GetStoredDefinitionAsync(TContext context, string jobName, CancellationToken cancellationToken) =>
		await context.Set<ImmediateJobDefinitionMetadataEntity>().AsNoTracking()
			.SingleOrDefaultAsync(definition => definition.Name == jobName, cancellationToken)
			?? throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");

	private async ValueTask UpdateDefinitionAsync(string jobName, bool paused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await ExecuteWithStrategyAsync(async token =>
		{
			await using var context = await contextFactory.CreateDbContextAsync(token);
			var metadata = await GetStoredDefinitionAsync(context, jobName, token);
			if (paused)
			{
				if (await PauseDefinitionAsync(context, metadata.Name, token) == 0)
				{
					await EnsureDefinitionAsync(metadata.Name, token);
					await PauseDefinitionAsync(context, metadata.Name, token);
				}

				return;
			}

			await EnsureDefinitionAsync(metadata.Name, token);
			await using var transaction = await context.Database.BeginTransactionAsync(token);
			var definition = await LockDefinitionAsync(context, metadata.Name, token);
			definition.IsPaused = false;
			await EvaluateDefinitionAsync(context, definition, ToRecord(metadata).AcquisitionLimits, _timeProvider.GetUtcNow(), additionalActive: 0, token);

			await context.SaveChangesAsync(token);
			await transaction.CommitAsync(token);
		}, cancellationToken);
	}

	private static Task<int> PauseDefinitionAsync(TContext context, string jobName, CancellationToken cancellationToken)
	{
		var stamp = Guid.NewGuid();
		return context.Set<ImmediateJobDefinitionEntity>().Where(item => item.JobName == jobName)
			.ExecuteUpdateAsync(update => update.SetProperty(item => item.IsPaused, valueExpression: true)
				.SetProperty(item => item.AcquisitionStatus, JobAcquisitionStatus.Paused)
				.SetProperty(item => item.NextEligibleAt, (DateTimeOffset?)null)
				.SetProperty(item => item.ConcurrencyStamp, stamp), cancellationToken);
	}

	private async ValueTask EnsureDefinitionsAsync(IEnumerable<string> jobNames, CancellationToken cancellationToken)
	{
		var names = jobNames.Distinct(StringComparer.Ordinal).ToList();
		if (names.Count == 0)
			return;
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var existing = await context.Set<ImmediateJobDefinitionEntity>()
			.Where(item => names.Contains(item.JobName)).Select(item => item.JobName)
			.ToListAsync(cancellationToken);
		var missing = names.Except(existing, StringComparer.Ordinal).ToList();
		if (missing.Count == 0)
			return;
		context.AddRange(missing.Select(static name => new ImmediateJobDefinitionEntity { JobName = name, ConcurrencyStamp = Guid.NewGuid() }));
		try
		{
			await context.SaveChangesAsync(cancellationToken);
		}
		catch (DbUpdateException)
		{
			// Another bootstrap may have won only some inserts. Recheck each missing row with a fresh context.
			foreach (var name in missing)
				await EnsureDefinitionAsync(name, cancellationToken);
		}
	}

	private async ValueTask EnsureDefinitionAsync(string jobName, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		if (await context.Set<ImmediateJobDefinitionEntity>().AnyAsync(item => item.JobName == jobName, cancellationToken))
			return;
		context.Add(new ImmediateJobDefinitionEntity { JobName = jobName, ConcurrencyStamp = Guid.NewGuid() });
		try
		{
			await context.SaveChangesAsync(cancellationToken);
		}

		catch (DbUpdateException)
		{
			await using var verification = await contextFactory.CreateDbContextAsync(cancellationToken);
			if (!await verification.Set<ImmediateJobDefinitionEntity>().AnyAsync(item => item.JobName == jobName, cancellationToken))
				throw;
		}
	}

	private static async Task<ImmediateJobDefinitionEntity> LockDefinitionAsync(TContext context, string jobName, CancellationToken cancellationToken)
	{
		// Take the same row's write lock before reading counters or active leases. Pause uses this lock too.
		// Generate the stamp locally: EF Core 8's SQLite provider cannot translate Guid.NewGuid().
		var stamp = Guid.NewGuid();
		await context.Set<ImmediateJobDefinitionEntity>().Where(item => item.JobName == jobName)
			.ExecuteUpdateAsync(update => update.SetProperty(item => item.ConcurrencyStamp, stamp), cancellationToken);
		return await context.Set<ImmediateJobDefinitionEntity>().SingleAsync(item => item.JobName == jobName, cancellationToken);
	}

	private static async Task<JobAcquisitionState> EvaluateDefinitionAsync(TContext context, ImmediateJobDefinitionEntity definition, JobAcquisitionLimits limits, DateTimeOffset now, int additionalActive, CancellationToken cancellationToken, bool includeActiveCount = false)
	{
		var acquisitions = limits.SlidingWindowPeriod is { } period
			? await context.Set<ImmediateJobAcquisitionEntity>().Where(item => item.JobName == definition.JobName && item.AcquiredAt > now - period)
				.OrderBy(item => item.AcquiredAt).Select(item => item.AcquiredAt).ToListAsync(cancellationToken)
			: [];
		var activeCount = includeActiveCount || limits.MaxConcurrency > 0
			? await context.Set<ImmediateJobEntity>().CountAsync(job => job.JobName == definition.JobName && job.State == JobState.Active && job.LeaseExpiresAt > now, cancellationToken)
			: 0;
		var state = JobAcquisitionEvaluator.Evaluate(definition.JobName, definition.IsPaused, limits, now,
			acquisitions, definition.FixedWindowStart, definition.FixedWindowCount, activeCount + additionalActive);
		definition.AcquisitionStatus = state.AcquisitionStatus;
		definition.NextEligibleAt = state.NextEligibleAt;
		return state;
	}

	private static async ValueTask<JobAcquisitionLimits?> ReserveDefinitionAsync(TContext context, string jobName, DateTimeOffset now, CancellationToken cancellationToken)
	{
		var definition = await LockDefinitionAsync(context, jobName, cancellationToken);
		var metadata = await context.Set<ImmediateJobDefinitionMetadataEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.Name == jobName, cancellationToken);
		if (metadata is null)
			return null;

		var limits = ToRecord(metadata).AcquisitionLimits;
		var state = await EvaluateDefinitionAsync(context, definition, limits, now, additionalActive: 0, cancellationToken);
		if (state.AcquisitionStatus != JobAcquisitionStatus.Ready)
			return null;
		if (limits.SlidingWindowPeriod is { } period)
		{
			await context.Set<ImmediateJobAcquisitionEntity>()
				.Where(item => item.JobName == jobName && item.AcquiredAt <= now - period).ExecuteDeleteAsync(cancellationToken);
			context.Add(new ImmediateJobAcquisitionEntity { Id = Guid.NewGuid(), JobName = jobName, AcquiredAt = now });
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

		return limits;
	}

	private async ValueTask<List<string>> FilterAcquisitionNamesAsync(Dictionary<string, int> capacities, CancellationToken cancellationToken)
	{
		var candidates = capacities.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToList();
		if (candidates.Count == 0)
			return [];

		return await ReadWithStrategyAsync(async (context, token) =>
		{
			// Advisory only: claim-time reservation still locks and rechecks the durable definition.
			var states = await ReadDefinitionStatusesAsync(context,
				context.Set<ImmediateJobDefinitionMetadataEntity>().Where(item => candidates.Contains(item.Name)),
				_timeProvider.GetUtcNow(), token, includeActiveCount: false);
			return states.Where(static state => state.AcquisitionStatus == JobAcquisitionStatus.Ready)
				.Select(static state => state.JobName)
				.ToList();
		}, cancellationToken);
	}

	private static async Task RefreshAcquiredDefinitionAsync(TContext context, string jobName, JobAcquisitionLimits limits, DateTimeOffset now, CancellationToken cancellationToken)
	{
		var definition = await context.Set<ImmediateJobDefinitionEntity>().FindAsync([jobName], cancellationToken)
			?? throw new DbUpdateConcurrencyException();
		await EvaluateDefinitionAsync(context, definition, limits, now, additionalActive: 0, cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
	}

	[LoggerMessage(
		EventId = LibraryEventIds.PauseJobAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.PauseJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseJobAsync called (JobName={JobName})"
	)]
	private partial void PauseJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.ResumeJobAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.ResumeJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeJobAsync called (JobName={JobName})"
	)]
	private partial void ResumeJobAsyncCalled(string jobName);

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobAcquisitionStateAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.GetJobAcquisitionStateAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobAcquisitionStateAsync called (JobName={JobName})"
	)]
	private partial void GetJobAcquisitionStateAsyncCalled(string jobName);
}

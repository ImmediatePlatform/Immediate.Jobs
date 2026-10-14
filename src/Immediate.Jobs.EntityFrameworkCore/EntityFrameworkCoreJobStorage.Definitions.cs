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
			var metadata = await GetStoredDefinitionAsync(context, jobName, token);
			var definition = await context.Set<ImmediateJobDefinitionEntity>().AsNoTracking()
				.SingleOrDefaultAsync(item => item.JobName == metadata.Name, token) ?? new() { JobName = metadata.Name };
			return await EvaluateDefinitionAsync(context, definition, ToRecord(metadata).AcquisitionLimits, _timeProvider.GetUtcNow(), additionalActive: 0, token, includeActiveCount: true);
		}, cancellationToken);
	}

	private static async Task<ImmediateJobDefinitionMetadataEntity> GetStoredDefinitionAsync(TContext context, string jobName, CancellationToken cancellationToken)
	{
		var query = context.Set<ImmediateJobDefinitionMetadataEntity>().AsNoTracking();
		// SQLite's NOCASE collation only handles ASCII; compare Unicode aliases in the client.
		if (context.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) != true)
			query = query.Where(definition => definition.Name == jobName);

		var definitions = await query.ToListAsync(cancellationToken);
		return definitions.SingleOrDefault(definition => string.Equals(definition.Name, jobName, StringComparison.OrdinalIgnoreCase))
			?? throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");
	}

	private async ValueTask UpdateDefinitionAsync(string jobName, bool paused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await ExecuteWithStrategyAsync(async token =>
		{
			await using var context = await contextFactory.CreateDbContextAsync(token);
			var metadata = await GetStoredDefinitionAsync(context, jobName, token);
			await EnsureDefinitionAsync(metadata.Name, token);
			await using var transaction = await context.Database.BeginTransactionAsync(token);
			var definition = await LockDefinitionAsync(context, metadata.Name, token);
			definition.IsPaused = paused;
			if (paused)
			{
				definition.AcquisitionStatus = JobAcquisitionStatus.Paused;
				definition.NextEligibleAt = null;
			}
			else
			{
				await EvaluateDefinitionAsync(context, definition, ToRecord(metadata).AcquisitionLimits, _timeProvider.GetUtcNow(), additionalActive: 0, token);
			}

			await context.SaveChangesAsync(token);
			await transaction.CommitAsync(token);
		}, cancellationToken);
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
			// This is an advisory read. The definition row is locked and rechecked when claiming a job.
			var query =
				from metadata in context.Set<ImmediateJobDefinitionMetadataEntity>().AsNoTracking()
				join definition in context.Set<ImmediateJobDefinitionEntity>().AsNoTracking() on metadata.Name equals definition.JobName into states
				from definition in states.DefaultIfEmpty()
				select new { Metadata = metadata, State = definition };
			if (context.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) != true)
				query = query.Where(item => candidates.Contains(item.Metadata.Name));

			var stored = await query.ToListAsync(token);
			var names = new List<string>();
			var now = _timeProvider.GetUtcNow();
			foreach (var item in stored)
			{
				if (!capacities.TryGetValue(item.Metadata.Name, out var capacity) || capacity <= 0)
					continue;

				var definition = item.State ?? new() { JobName = item.Metadata.Name };
				var state = await EvaluateDefinitionAsync(context, definition, ToRecord(item.Metadata).AcquisitionLimits, now, additionalActive: 0, token);
				if (state.AcquisitionStatus == JobAcquisitionStatus.Ready)
					names.Add(item.Metadata.Name);
			}

			return names;
		}, cancellationToken);
	}

	private static async Task RefreshAcquiredDefinitionAsync(TContext context, string jobName, JobAcquisitionLimits limits, DateTimeOffset now, CancellationToken cancellationToken)
	{
		var definition = await context.Set<ImmediateJobDefinitionEntity>().SingleAsync(item => item.JobName == jobName, cancellationToken);
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

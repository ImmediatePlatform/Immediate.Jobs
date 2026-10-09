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
		return await ReadWithStrategyAsync(async (context, token) =>
		{
			var definition = await context.Set<ImmediateJobDefinitionEntity>().AsNoTracking()
				.SingleOrDefaultAsync(item => item.JobName == jobName, token) ?? new() { JobName = jobName };
			return await EvaluateDefinitionAsync(context, definition, limits, _timeProvider.GetUtcNow(), additionalActive: 0, token, includeActiveCount: true);
		}, cancellationToken);
	}

	private async ValueTask UpdateDefinitionAsync(string jobName, JobAcquisitionLimits limits, bool paused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		limits.Validate();
		await EnsureDefinitionAsync(jobName, cancellationToken);
		await ExecuteWithStrategyAsync(async token =>
		{
			await using var context = await contextFactory.CreateDbContextAsync(token);
			await using var transaction = await context.Database.BeginTransactionAsync(token);
			var definition = await LockDefinitionAsync(context, jobName, token);
			definition.IsPaused = paused;
			_ = await EvaluateDefinitionAsync(context, definition, limits, _timeProvider.GetUtcNow(), additionalActive: 0, token);
			_ = await context.SaveChangesAsync(token);
			await transaction.CommitAsync(token);
		}, cancellationToken);
	}

	private async ValueTask EnsureDefinitionAsync(string jobName, CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		if (await context.Set<ImmediateJobDefinitionEntity>().AnyAsync(item => item.JobName == jobName, cancellationToken))
			return;
		_ = context.Add(new ImmediateJobDefinitionEntity { JobName = jobName, ConcurrencyStamp = Guid.NewGuid() });
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken);
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
		_ = await context.Set<ImmediateJobDefinitionEntity>().Where(item => item.JobName == jobName)
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

	private static async ValueTask<bool> ReserveDefinitionAsync(TContext context, string jobName, JobAcquisitionRequest? request, DateTimeOffset now, CancellationToken cancellationToken)
	{
		var limits = request?.LimitsFor(jobName) ?? new();
		var definition = await LockDefinitionAsync(context, jobName, cancellationToken);
		var state = await EvaluateDefinitionAsync(context, definition, limits, now, additionalActive: 0, cancellationToken);
		if (state.AcquisitionStatus != JobAcquisitionStatus.Ready)
			return false;
		if (limits.SlidingWindowPeriod is { } period)
		{
			_ = await context.Set<ImmediateJobAcquisitionEntity>()
				.Where(item => item.JobName == jobName && item.AcquiredAt <= now - period).ExecuteDeleteAsync(cancellationToken);
			_ = context.Add(new ImmediateJobAcquisitionEntity { Id = Guid.NewGuid(), JobName = jobName, AcquiredAt = now });
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

		return true;
	}

	private async ValueTask<List<string>> FilterAcquisitionNamesAsync(Dictionary<string, int> capacities, JobAcquisitionRequest request, CancellationToken cancellationToken)
	{
		var candidates = capacities.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToList();
		if (candidates.Count == 0)
			return [];

		return await ReadWithStrategyAsync(async (context, token) =>
		{
			// This is an advisory read. The definition row is locked and rechecked when claiming a job.
			var stored = await context.Set<ImmediateJobDefinitionEntity>().AsNoTracking()
				.Where(item => candidates.Contains(item.JobName)).ToListAsync(token);
			var definitions = stored.ToDictionary(static item => item.JobName, StringComparer.Ordinal);
			var names = new List<string>();
			var now = _timeProvider.GetUtcNow();
			foreach (var name in candidates)
			{
				var definition = definitions.GetValueOrDefault(name) ?? new() { JobName = name };
				var state = await EvaluateDefinitionAsync(context, definition, request.LimitsFor(name), now, additionalActive: 0, token);
				if (state.AcquisitionStatus == JobAcquisitionStatus.Ready)
					names.Add(name);
			}

			return names;
		}, cancellationToken);
	}

	private static async Task RefreshAcquiredDefinitionAsync(TContext context, string jobName, JobAcquisitionRequest? request, DateTimeOffset now, CancellationToken cancellationToken)
	{
		var definition = await context.Set<ImmediateJobDefinitionEntity>().SingleAsync(item => item.JobName == jobName, cancellationToken);
		_ = await EvaluateDefinitionAsync(context, definition, request?.LimitsFor(jobName) ?? new(), now, additionalActive: 0, cancellationToken);
		_ = await context.SaveChangesAsync(cancellationToken);
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

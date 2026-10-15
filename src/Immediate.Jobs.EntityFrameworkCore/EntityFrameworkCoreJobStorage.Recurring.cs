using Immediate.Jobs.Shared.Apis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		UpsertRecurringAsyncCalled(schedule.Name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		var definitions = await GetDefinitionNamesAsync(context, [schedule.JobName], cancellationToken);
		if (definitions.TryGetValue(schedule.JobName, out var name))
			schedule = schedule with { JobName = name };

		if (context.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
		{
			await UpsertSqliteRecurringAsync(context, schedule, cancellationToken);
			return;
		}

		if (await UpdateRecurringAsync(context, schedule, cancellationToken) != 0)
			return;
		await ThrowIfReplacingCodeDefinedScheduleAsync(context, schedule, cancellationToken);

		context.Add(ToEntity(schedule));
		try
		{
			await context.SaveChangesAsync(cancellationToken);
		}
		catch (DbUpdateException)
		{
			// A competing node inserted the same schedule after our update attempt.
			await using var retryContext = await contextFactory.CreateDbContextAsync(cancellationToken);
			if (await UpdateRecurringAsync(retryContext, schedule, cancellationToken) != 0)
				return;
			await ThrowIfReplacingCodeDefinedScheduleAsync(retryContext, schedule, cancellationToken);
			throw;
		}
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		RemoveRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		name = await ResolveRecurringNameAsync(context, name, cancellationToken);
		var removed = await context.Set<ImmediateRecurringJobEntity>()
			.Where(schedule => schedule.Name == name && !schedule.IsCodeDefined)
			.ExecuteDeleteAsync(cancellationToken);
		if (removed != 0)
			return;
		if (await context.Set<ImmediateRecurringJobEntity>()
			.AnyAsync(schedule => schedule.Name == name, cancellationToken))
		{
			throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");
		}

		throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		PauseRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await MutateRecurringAsync(name, schedule => schedule.IsPaused = true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ResumeRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await MutateRecurringAsync(name, schedule =>
		{
			if (schedule.CancellationReason is not null)
				throw new ImmediateJobException("Canceled recurring schedules cannot be resumed.");
			schedule.IsPaused = false;
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		IReadOnlyList<string> jobNames,
		CancellationToken cancellationToken = default
	)
	{
		GetDueRecurringAsyncCalled(batchSize);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		if (context.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
		{
			var storedNames = await context.Set<ImmediateRecurringJobEntity>().AsNoTracking()
				.Where(schedule => !schedule.IsPaused && schedule.NextRunAt <= now)
				.Select(schedule => schedule.JobName).Distinct().ToListAsync(cancellationToken);
			jobNames = storedNames.Where(name => jobNames.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
		}

		return await RecurringWithDefinitions(context)
			.AsNoTracking()
			.Where(schedule => !schedule.IsPaused && schedule.NextRunAt <= now && jobNames.Contains(schedule.JobName))
			.OrderBy(schedule => schedule.NextRunAt)
			.Take(batchSize)
			.Select(schedule => new RecurringJobSchedule
			{
				Name = schedule.Name,
				JobName = schedule.JobName,
				QueueName = schedule.QueueName,
				CancellationReason = schedule.CancellationReason,
				Cron = schedule.Cron,
				TimeZone = schedule.TimeZone,
				IsCodeDefined = schedule.IsCodeDefined,
				IsPaused = schedule.IsPaused,
				NextRunAt = schedule.NextRunAt,
				LastRunAt = schedule.LastRunAt,
			})
			.ToListAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		IReadOnlyList<JobContinuationEdge>? dependencies = null,
		CancellationToken cancellationToken = default
	)
	{
		MaterializeRecurringAsyncCalled(job.JobHandle, schedule.Name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
		var strategy = strategyContext.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(
			operationCancellationToken => MaterializeRecurringCoreAsync(
				schedule,
				job,
				nextRunAt,
				dependencies,
				operationCancellationToken
			),
			cancellationToken
		);
	}

	private async Task<bool> MaterializeRecurringCoreAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		IReadOnlyList<JobContinuationEdge>? dependencies,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		var name = await ResolveRecurringNameAsync(context, schedule.Name, cancellationToken);
		var entity = await context.Set<ImmediateRecurringJobEntity>()
			.SingleOrDefaultAsync(item => item.Name == name, cancellationToken);

		if (entity is null || entity.IsPaused || entity.NextRunAt != schedule.NextRunAt)
			return false;

		entity.LastRunAt = schedule.NextRunAt;
		entity.NextRunAt = nextRunAt;
		entity.ConcurrencyStamp = Guid.NewGuid();
		var jobEntities = new[] { ToEntity(job) }.ToDictionary(static item => item.Id, StringComparer.Ordinal);
		var edgeEntities = dependencies?.Select(ToEntity).ToList() ?? [];
		await EvaluateInitialDependenciesAsync(
			context,
			jobEntities,
			edgeEntities,
			_timeProvider.GetUtcNow(),
			cancellationToken
		);
		context.AddRange(jobEntities.Values);

		if (edgeEntities.Count != 0)
		{
			context.AddRange(edgeEntities);
		}

		try
		{
			await context.SaveChangesAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			return true;
		}
		catch (DbUpdateException)
		{
			await transaction.RollbackAsync(cancellationToken);
			if (job.RecurringKey is not null)
			{
				await AdvanceRecurringAfterDedupeAsync(
					schedule,
					job.RecurringKey,
					nextRunAt,
					cancellationToken
				);
			}

			return false;
		}
	}

	private async Task AdvanceRecurringAfterDedupeAsync(
		RecurringJobSchedule schedule,
		string recurringKey,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken
	)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		if (!await context.Set<ImmediateJobEntity>()
			.AnyAsync(job => job.RecurringKey == recurringKey, cancellationToken))
		{
			return;
		}

		var name = await ResolveRecurringNameAsync(context, schedule.Name, cancellationToken);
		var concurrencyStamp = Guid.NewGuid();
		await context.Set<ImmediateRecurringJobEntity>()
			.Where(entity =>
				entity.Name == name &&
				!entity.IsPaused &&
				entity.NextRunAt == schedule.NextRunAt)
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(entity => entity.LastRunAt, schedule.NextRunAt)
				.SetProperty(entity => entity.NextRunAt, nextRunAt)
				.SetProperty(entity => entity.ConcurrencyStamp, concurrencyStamp),
				cancellationToken);
	}

	private async ValueTask MutateRecurringAsync(string name, Action<ImmediateRecurringJobEntity> mutate, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		name = await ResolveRecurringNameAsync(context, name, cancellationToken);
		var schedule = await context.Set<ImmediateRecurringJobEntity>().FindAsync([name], cancellationToken) ?? throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
		mutate(schedule);
		schedule.ConcurrencyStamp = Guid.NewGuid();
		await context.SaveChangesAsync(cancellationToken);
	}

	private static async Task<int> UpdateRecurringAsync(
		TContext context,
		RecurringJobSchedule schedule,
		CancellationToken cancellationToken
	)
	{
		var name = await ResolveRecurringNameAsync(context, schedule.Name, cancellationToken);
		var concurrencyStamp = Guid.NewGuid();
		return await context.Set<ImmediateRecurringJobEntity>()
			.Where(entity => entity.Name == name && (schedule.IsCodeDefined || !entity.IsCodeDefined))
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(entity => entity.JobName, schedule.JobName)

				.SetProperty(entity => entity.CancellationReason, entity => schedule.CancellationReason ?? entity.CancellationReason)
				.SetProperty(entity => entity.Cron, schedule.Cron)
				.SetProperty(entity => entity.TimeZone, schedule.TimeZone)
				.SetProperty(entity => entity.IsCodeDefined, schedule.IsCodeDefined)
				.SetProperty(entity => entity.NextRunAt, schedule.NextRunAt)
				.SetProperty(entity => entity.ConcurrencyStamp, concurrencyStamp),
				cancellationToken);
	}

	private static async Task ThrowIfReplacingCodeDefinedScheduleAsync(
		TContext context,
		RecurringJobSchedule schedule,
		CancellationToken cancellationToken
	)
	{
		var name = await ResolveRecurringNameAsync(context, schedule.Name, cancellationToken);
		if (!schedule.IsCodeDefined && await context.Set<ImmediateRecurringJobEntity>()
			.AnyAsync(entity => entity.Name == name && entity.IsCodeDefined, cancellationToken))
		{
			throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules.");
		}
	}

	private async Task UpsertSqliteRecurringAsync(TContext context, RecurringJobSchedule schedule, CancellationToken cancellationToken)
	{
		await EnsureDefinitionCatalogAsync(cancellationToken);
		await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		// Serialize Unicode name resolution with catalogue writes and other schedule inserts.
		var stamp = Guid.NewGuid();
		await context.Set<ImmediateJobDefinitionCatalogEntity>().Where(item => item.Id == 1)
			.ExecuteUpdateAsync(update => update.SetProperty(item => item.ConcurrencyStamp, stamp), cancellationToken);
		if (await UpdateRecurringAsync(context, schedule, cancellationToken) == 0)
		{
			await ThrowIfReplacingCodeDefinedScheduleAsync(context, schedule, cancellationToken);
			context.Add(ToEntity(schedule));
			await context.SaveChangesAsync(cancellationToken);
		}

		await transaction.CommitAsync(cancellationToken);
	}

	private static async Task<string> ResolveRecurringNameAsync(
		TContext context, string name, CancellationToken cancellationToken
	)
	{
		if (context.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) != true)
			return name;

		// SQLite NOCASE folds ASCII only; retain the stored spelling for Unicode aliases.
		var names = await context.Set<ImmediateRecurringJobEntity>()
			.AsNoTracking()
			.Select(schedule => schedule.Name)
			.ToListAsync(cancellationToken);
		return names.FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) ?? name;
	}

	[LoggerMessage(
		EventId = LibraryEventIds.UpsertRecurringAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.UpsertRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpsertRecurringAsync called (Schedule={Schedule})"
	)]
	private partial void UpsertRecurringAsyncCalled(string schedule);

	[LoggerMessage(
		EventId = LibraryEventIds.RemoveRecurringAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.RemoveRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RemoveRecurringAsync called (Name={Name})"
	)]
	private partial void RemoveRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.PauseRecurringAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.PauseRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseRecurringAsync called (Name={Name})"
	)]
	private partial void PauseRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.ResumeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.ResumeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeRecurringAsync called (Name={Name})"
	)]
	private partial void ResumeRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.GetDueRecurringAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.GetDueRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetDueRecurringAsync called (BatchSize={BatchSize})"
	)]
	private partial void GetDueRecurringAsyncCalled(int batchSize);

	[LoggerMessage(
		EventId = LibraryEventIds.MaterializeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.MaterializeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MaterializeRecurringAsync called (JobHandle={JobHandle}, Schedule={Schedule})"
	)]
	private partial void MaterializeRecurringAsyncCalled(JobHandle jobHandle, string schedule);
}

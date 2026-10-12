using System.Data.Common;
using Immediate.Jobs.Shared.Apis;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(
		RecurringJobSchedule schedule,
		CancellationToken cancellationToken = default
	)
	{
		UpsertRecurringAsyncCalled(schedule.Name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await RetryConcurrencyAsync(
			Core,
			cancellationToken
		);

		async Task Core(T connection)
		{
			var existing = await Recurring(connection).SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken);
			if (existing is null)
			{
				try
				{
					_ = await InsertAsync(connection, ToEntity(schedule), cancellationToken);
					return;
				}
				catch (DbException)
				{
					existing = await Recurring(connection).SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken);
					if (existing is null)
						throw;
				}
			}

			if (!schedule.IsCodeDefined && existing.IsCodeDefined)
				throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules.");

			var oldStamp = existing.ConcurrencyStamp;
			existing.JobName = schedule.JobName;
			existing.QueueName = schedule.QueueName;
			existing.Cron = schedule.Cron;
			existing.TimeZone = schedule.TimeZone;
			existing.IsCodeDefined = schedule.IsCodeDefined;
			existing.NextRunAt = schedule.NextRunAt;
			existing.ConcurrencyStamp = Guid.NewGuid();

			if (!await UpdateRecurringAsync(connection, existing, oldStamp, cancellationToken))
				throw new LostRaceException();
		}
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		RemoveRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var removed = await Recurring(connection)
			.Where(schedule => schedule.Name == name && !schedule.IsCodeDefined)
			.DeleteAsync(cancellationToken);
		if (removed != 0)
			return;
		if (await Recurring(connection).AnyAsync(schedule => schedule.Name == name, cancellationToken))
			throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");
		throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		PauseRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await SetRecurringPausedAsync(name, paused: true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ResumeRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await SetRecurringPausedAsync(name, paused: false, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		CancellationToken cancellationToken = default
	)
	{
		GetDueRecurringAsyncCalled(batchSize);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var schedules = await Recurring(connection)
			.Where(schedule => !schedule.IsPaused && schedule.NextRunAt <= now)
			.OrderBy(schedule => schedule.NextRunAt)
			.Take(batchSize)
			.ToListAsync(cancellationToken);
		return [.. schedules.Select(ToRecord)];
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

		await using var scope = contextScope.GetScope(out var connection);

		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
		try
		{
			var entity = await Recurring(connection).SingleOrDefaultAsync(item => item.Name == schedule.Name, cancellationToken);
			if (entity is null || entity.IsPaused || entity.NextRunAt != schedule.NextRunAt)
			{
				return false;
			}

			var oldStamp = entity.ConcurrencyStamp;
			entity.LastRunAt = schedule.NextRunAt;
			entity.NextRunAt = nextRunAt;
			entity.ConcurrencyStamp = Guid.NewGuid();

			if (!await UpdateRecurringAsync(connection, entity, oldStamp, cancellationToken))
				throw new LostRaceException();

			var jobEntities = new[] { ToEntity(job) }.ToDictionary(static item => item.Id, StringComparer.Ordinal);
			var edgeEntities = dependencies?.Select(ToEntity).ToList() ?? [];
			await EvaluateInitialDependenciesAsync(
				connection,
				jobEntities,
				edgeEntities,
				timeProvider.GetUtcNow(),
				cancellationToken
			);
			await InsertAsync(connection, jobEntities.Values.Single(), cancellationToken);

			if (edgeEntities.Count != 0)
			{
				foreach (var edge in edgeEntities)
					await InsertAsync(connection, edge, cancellationToken);
			}

			await connection.CommitTransactionAsync(cancellationToken);
			return true;
		}
		catch (Exception exception) when (exception is LostRaceException or DbException)
		{
			await connection.RollbackTransactionAsync(cancellationToken);
			if (exception is DbException && job.RecurringKey is not null)
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

	private async ValueTask SetRecurringPausedAsync(string name, bool paused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		await using var scope = contextScope.GetScope(out var connection);

		var updated = await Recurring(connection)
			.Where(schedule => schedule.Name == name)
			.Set(schedule => schedule.IsPaused, paused)
			.Set(schedule => schedule.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken);
		if (updated == 0)
			throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
	}

	private async ValueTask AdvanceRecurringAfterDedupeAsync(
		RecurringJobSchedule schedule,
		string recurringKey,
		DateTimeOffset nextRunAt,
		CancellationToken cancellationToken
	)
	{
		await using var scope = contextScope.GetScope(out var connection);

		if (!await Jobs(connection)
			.AnyAsync(job => job.RecurringKey == recurringKey, cancellationToken))
		{
			return;
		}

		_ = await Recurring(connection)
			.Where(entity =>
				entity.Name == schedule.Name &&
				!entity.IsPaused &&
				entity.NextRunAt == schedule.NextRunAt)
			.Set(entity => entity.LastRunAt, schedule.NextRunAt)
			.Set(entity => entity.NextRunAt, nextRunAt)
			.Set(entity => entity.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken);
	}

	private async Task<bool> UpdateRecurringAsync(
		DataConnection connection,
		ImmediateRecurringJobEntity schedule,
		Guid oldStamp,
		CancellationToken cancellationToken
	)
	{
		var updated = await Recurring(connection)
			.Where(entity => entity.Name == schedule.Name && entity.ConcurrencyStamp == oldStamp)
			.Set(entity => entity.JobName, schedule.JobName)
			.Set(entity => entity.QueueName, schedule.QueueName)
			.Set(entity => entity.Cron, schedule.Cron)
			.Set(entity => entity.TimeZone, schedule.TimeZone)
			.Set(entity => entity.IsCodeDefined, schedule.IsCodeDefined)
			.Set(entity => entity.IsPaused, schedule.IsPaused)
			.Set(entity => entity.NextRunAt, schedule.NextRunAt)
			.Set(entity => entity.LastRunAt, schedule.LastRunAt)
			.Set(entity => entity.ConcurrencyStamp, schedule.ConcurrencyStamp)
			.UpdateAsync(cancellationToken);
		return updated != 0;
	}

	[LoggerMessage(
		EventId = LibraryEventIds.UpsertRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.UpsertRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpsertRecurringAsync called (Schedule={Schedule})"
	)]
	private partial void UpsertRecurringAsyncCalled(string schedule);

	[LoggerMessage(
		EventId = LibraryEventIds.RemoveRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.RemoveRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RemoveRecurringAsync called (Name={Name})"
	)]
	private partial void RemoveRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.PauseRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.PauseRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseRecurringAsync called (Name={Name})"
	)]
	private partial void PauseRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.ResumeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.ResumeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeRecurringAsync called (Name={Name})"
	)]
	private partial void ResumeRecurringAsyncCalled(string name);

	[LoggerMessage(
		EventId = LibraryEventIds.GetDueRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetDueRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetDueRecurringAsync called (BatchSize={BatchSize})"
	)]
	private partial void GetDueRecurringAsyncCalled(int batchSize);

	[LoggerMessage(
		EventId = LibraryEventIds.MaterializeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.MaterializeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MaterializeRecurringAsync called (JobHandle={JobHandle}, Schedule={Schedule})"
	)]
	private partial void MaterializeRecurringAsyncCalled(JobHandle jobHandle, string schedule);
}

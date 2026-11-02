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
			var query = Recurring(connection).Where(item => item.Name == schedule.Name && item.IsCodeDefined == schedule.IsCodeDefined);
			int updated;
			if (schedule.IsCodeDefined)
			{
				updated = await query.Set(item => item.NextRunAt, schedule.NextRunAt)
					.Set(item => item.ConcurrencyStamp, Guid.NewGuid()).UpdateAsync(cancellationToken);
			}
			else
			{
				var definitions = await GetDefinitionNamesAsync(connection, [schedule.JobName], cancellationToken);
				if (definitions.TryGetValue(schedule.JobName, out var jobName))
					schedule = schedule with { JobName = jobName };
				updated = await query.Set(item => item.JobName, schedule.JobName)
					.Set(item => item.CancellationReason, item => schedule.CancellationReason ?? item.CancellationReason)
					.Set(item => item.IsPaused, item => item.IsPaused || schedule.CancellationReason != null || item.CancellationReason != null)
					.Set(item => item.Cron, schedule.Cron).Set(item => item.TimeZone, schedule.TimeZone)
					.Set(item => item.NextRunAt, schedule.NextRunAt)
					.Set(item => item.ConcurrencyStamp, Guid.NewGuid()).UpdateAsync(cancellationToken);
			}

			if (updated != 0)
				return;
			var ownership = await Recurring(connection).Where(item => item.Name == schedule.Name)
				.Select(item => (bool?)item.IsCodeDefined).SingleOrDefaultAsync(cancellationToken);
			if (ownership is { } isCodeDefined)
			{
				if (isCodeDefined != schedule.IsCodeDefined)
					throw new ImmediateJobException("Recurring schedule ownership cannot be changed by upsert.");
				// A same-owner insert can win between our update and this diagnostic read.
				throw new LostRaceException();
			}

			if (schedule.IsCodeDefined)
				throw new ImmediateJobException("Code-defined recurring schedules must be created by definition reconciliation.");
			try
			{
				await InsertAsync(connection, ToEntity(schedule), cancellationToken);
			}
			catch (DbException exception) when (exception.SqlState is "23505")
			{
				// PostgreSQL requires a rollback before any query after a unique-key violation.
				throw new LostRaceException();
			}
			catch (DbException)
			{
				if (await Recurring(connection).AnyAsync(item => item.Name == schedule.Name, cancellationToken))
					throw new LostRaceException();
				throw;
			}
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
		IReadOnlyList<string> jobNames,
		CancellationToken cancellationToken = default
	)
	{
		GetDueRecurringAsyncCalled(batchSize);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		var schedules = await Recurring(connection)
			.Where(schedule => !schedule.IsPaused && schedule.NextRunAt <= now && jobNames.Contains(schedule.JobName))
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
			var updated = await Recurring(connection)
				.Where(entity => entity.Name == schedule.Name && !entity.IsPaused && entity.NextRunAt == schedule.NextRunAt)
				.Set(entity => entity.LastRunAt, schedule.NextRunAt)
				.Set(entity => entity.NextRunAt, nextRunAt)
				.Set(entity => entity.ConcurrencyStamp, Guid.NewGuid())
				.UpdateAsync(cancellationToken);
			if (updated == 0)
				return false;

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
			.Where(schedule => schedule.Name == name && (paused || schedule.CancellationReason == null))
			.Set(schedule => schedule.IsPaused, paused)
			.Set(schedule => schedule.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken);
		if (updated != 0)
			return;
		if (!paused && await Recurring(connection).AnyAsync(schedule => schedule.Name == name && schedule.CancellationReason != null, cancellationToken))
			throw new ImmediateJobException("Canceled recurring schedules cannot be resumed.");
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

		await Recurring(connection)
			.Where(entity =>
				entity.Name == schedule.Name &&
				!entity.IsPaused &&
				entity.NextRunAt == schedule.NextRunAt &&
				Jobs(connection).Any(job => job.RecurringKey == recurringKey))
			.Set(entity => entity.LastRunAt, schedule.NextRunAt)
			.Set(entity => entity.NextRunAt, nextRunAt)
			.Set(entity => entity.ConcurrencyStamp, Guid.NewGuid())
			.UpdateAsync(cancellationToken);
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

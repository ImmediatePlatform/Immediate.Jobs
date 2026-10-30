using System.Text.Json;
using System.Data;
using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.EntityFrameworkCore;

internal sealed partial class EntityFrameworkCoreJobStorage<TContext>
	where TContext : DbContext
{
	/// <inheritdoc />
	public async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
	{
		MergeJobDefinitionsListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		var reports = new List<(string Name, int Jobs, int Schedules)>();
		await RetryConcurrencyAsync(async token =>
		{
			await EnsureDefinitionCatalogAsync(token);
			await using var context = await contextFactory.CreateDbContextAsync(token);
			await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
			// A singleton row serializes the complete read/merge/write across application instances.
			var stamp = Guid.NewGuid();
			await context.Set<ImmediateJobDefinitionCatalogEntity>().Where(item => item.Id == 1)
				.ExecuteUpdateAsync(update => update.SetProperty(item => item.ConcurrencyStamp, stamp), token);
			var definitions = await context.Set<ImmediateJobDefinitionMetadataEntity>()
				.ToDictionaryAsync(static item => item.Name, StringComparer.OrdinalIgnoreCase, token);
			var schedules = await context.Set<ImmediateRecurringJobEntity>()
				.ToDictionaryAsync(static item => item.Name, StringComparer.OrdinalIgnoreCase, token);
			var changes = JobDefinitionReconciliation.Create(registration,
				definitions.Values.Select(ToRecord).ToList(),
				schedules.Values.Select(ToDefinitionSchedule).ToList());
			reports.Clear();
			var removed = changes.RemovedDefinitionNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var jobQuery = context.Set<ImmediateJobEntity>().Where(job => job.State != JobState.Succeeded
				&& job.State != JobState.Failed && job.State != JobState.Cancelled && job.State != JobState.Skipped
				&& removed.Contains(job.JobName));
			var jobs = removed.Count == 0 ? [] : await jobQuery.ToListAsync(token);
			var cancelledSchedules = schedules.Values.Where(schedule => !schedule.IsCodeDefined
				&& removed.Contains(schedule.JobName) && schedule.CancellationReason is null).ToList();
			var now = _timeProvider.GetUtcNow();
			foreach (var job in jobs)
				await MarkCancelledAsync(context, job, now, JobDefinitionReconciliation.RemovalCancellationReason, token);
			foreach (var job in jobs)
				await PropagateTerminalAsync(context, job, now, token);
			foreach (var schedule in cancelledSchedules)
			{
				schedule.CancellationReason = JobDefinitionReconciliation.RemovalCancellationReason;
				schedule.IsPaused = true;
				schedule.ConcurrencyStamp = Guid.NewGuid();
			}

			foreach (var name in changes.RemovedDefinitionNames)
			{
				context.Remove(definitions[name]);
				reports.Add((name, jobs.Count(job => string.Equals(job.JobName, name, StringComparison.OrdinalIgnoreCase)),
					cancelledSchedules.Count(schedule => string.Equals(schedule.JobName, name, StringComparison.OrdinalIgnoreCase))));
			}

			foreach (var definition in changes.Definitions)
			{
				var entity = ToEntity(definition);
				if (definitions.TryGetValue(definition.Name, out var current))
					context.Entry(current).CurrentValues.SetValues(entity);
				else
					context.Add(entity);
			}

			foreach (var name in changes.RemovedScheduleNames)
				context.Remove(schedules[name]);
			foreach (var schedule in changes.Schedules)
			{
				if (schedules.TryGetValue(schedule.Name, out var current))
					context.Entry(current).CurrentValues.SetValues(ToEntity(schedule));
				else
					context.Add(ToEntity(schedule));
			}

			await context.SaveChangesAsync(token);
			await context.Set<ImmediateFairQueueGroupEntity>()
				.Where(group => !JobsWithDefinitions(context).Any(job =>
					job.QueueName == group.QueueName && job.GroupId == group.GroupId
					&& (job.State == JobState.Pending || job.State == JobState.Scheduled || job.State == JobState.Active)))
				.ExecuteDeleteAsync(token);
			await transaction.CommitAsync(token);
		}, cancellationToken);
		foreach (var report in reports)
			DefinitionRemoved(report.Name, report.Jobs, report.Schedules);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		GetJobDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		return await ReadWithStrategyAsync(ReadJobDefinitionsAsync, cancellationToken);
	}

	private static async Task<IReadOnlyList<JobDefinitionRecord>> ReadJobDefinitionsAsync(TContext context, CancellationToken cancellationToken)
	{
		var definitions = await context.Set<ImmediateJobDefinitionMetadataEntity>()
			.AsNoTracking()
			.ToListAsync(cancellationToken);

		return [.. definitions.Select(ToRecord).OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)];
	}

	private async Task EnsureDefinitionCatalogAsync(CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		if (await context.Set<ImmediateJobDefinitionCatalogEntity>().AnyAsync(item => item.Id == 1, cancellationToken))
			return;
		context.Add(new ImmediateJobDefinitionCatalogEntity { Id = 1, ConcurrencyStamp = Guid.NewGuid() });
		try
		{
			await context.SaveChangesAsync(cancellationToken);
		}
		catch (DbUpdateException)
		{
			await using var verification = await contextFactory.CreateDbContextAsync(cancellationToken);
			if (!await verification.Set<ImmediateJobDefinitionCatalogEntity>().AnyAsync(item => item.Id == 1, cancellationToken))
				throw;
		}
	}

	private static RecurringJobSchedule ToDefinitionSchedule(ImmediateRecurringJobEntity entity) => new()
	{
		Name = entity.Name,
		JobName = entity.JobName,
		CancellationReason = entity.CancellationReason,
		Cron = entity.Cron,
		TimeZone = entity.TimeZone,
		IsCodeDefined = entity.IsCodeDefined,
		IsPaused = entity.IsPaused,
		NextRunAt = entity.NextRunAt,
		LastRunAt = entity.LastRunAt,
	};

	private static ImmediateJobDefinitionMetadataEntity ToEntity(JobDefinitionRecord definition) => new()
	{
		Name = definition.Name,
		QueueName = definition.QueueName,
		QueuePriority = definition.QueuePriority,
		QueueConcurrency = definition.QueueConcurrency,
		Cron = definition.Cron,
		TimeZone = definition.TimeZone,
		MaxAttempts = definition.MaxAttempts,
		Timeout = definition.Timeout?.ToString(),
		MaxConcurrency = definition.MaxConcurrency,
		OverlapPolicy = definition.OverlapPolicy,
		MisfireHandlingMode = definition.MisfireHandlingMode,
		Backoff = definition.Backoff,
		BackoffBase = definition.BackoffBase.ToString(),
		SlidingWindowMax = definition.SlidingWindowMax,
		SlidingWindowPeriod = definition.SlidingWindowPeriod?.ToString(),
		FixedWindowMax = definition.FixedWindowMax,
		FixedWindowPeriod = definition.FixedWindowPeriod?.ToString(),
		Tags = JsonSerializer.Serialize([.. definition.Tags], EntityFrameworkCoreJsonSerializerContext.Default.StringArray),
	};

	private static JobDefinitionRecord ToRecord(ImmediateJobDefinitionMetadataEntity entity) => new()
	{
		Name = entity.Name,
		QueueName = entity.QueueName,
		QueuePriority = entity.QueuePriority,
		QueueConcurrency = entity.QueueConcurrency,
		Cron = entity.Cron,
		TimeZone = entity.TimeZone,
		MaxAttempts = entity.MaxAttempts,
		Timeout = entity.Timeout is { } timeout ? TimeSpan.Parse(timeout, CultureInfo.InvariantCulture) : null,
		MaxConcurrency = entity.MaxConcurrency,
		OverlapPolicy = entity.OverlapPolicy,
		MisfireHandlingMode = entity.MisfireHandlingMode,
		Backoff = entity.Backoff,
		BackoffBase = TimeSpan.Parse(entity.BackoffBase, CultureInfo.InvariantCulture),
		SlidingWindowMax = entity.SlidingWindowMax,
		SlidingWindowPeriod = entity.SlidingWindowPeriod is { } slidingPeriod ? TimeSpan.Parse(slidingPeriod, CultureInfo.InvariantCulture) : null,
		FixedWindowMax = entity.FixedWindowMax,
		FixedWindowPeriod = entity.FixedWindowPeriod is { } fixedPeriod ? TimeSpan.Parse(fixedPeriod, CultureInfo.InvariantCulture) : null,
		Tags = JsonSerializer.Deserialize(entity.Tags, EntityFrameworkCoreJsonSerializerContext.Default.StringArray) ?? [],
	};

	[LoggerMessage(
		EventId = LibraryEventIds.DefinitionRemoved,
		EventName = "Immediate.Jobs.EntityFrameworkCore.DefinitionRemoved",
		Level = LogLevel.Warning,
		Message = "Job definition '{JobName}' removed; canceled {JobCount} invocations and {ScheduleCount} recurring schedules."
	)]
	private partial void DefinitionRemoved(string jobName, int jobCount, int scheduleCount);

	[LoggerMessage(
		EventId = LibraryEventIds.MergeJobDefinitionsListAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.MergeJobDefinitionsListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MergeJobDefinitionsListAsyncCalled"
	)]
	private partial void MergeJobDefinitionsListAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.GetJobDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobDefinitionsAsyncCalled"
	)]
	private partial void GetJobDefinitionsAsyncCalled();
}

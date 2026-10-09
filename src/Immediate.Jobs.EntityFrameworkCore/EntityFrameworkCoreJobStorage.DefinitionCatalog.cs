using System.Text.Json;
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
		// Validate before taking a write lock or creating the catalogue sentinel.
		_ = JobDefinitionReconciliation.Create(registration, [], []);
		await RetryConcurrencyAsync(async token =>
		{
			await EnsureDefinitionCatalogAsync(token);
			await using var context = await contextFactory.CreateDbContextAsync(token);
			await using var transaction = await context.Database.BeginTransactionAsync(token);
			// A singleton row serializes the complete read/merge/write across application instances.
			var stamp = Guid.NewGuid();
			_ = await context.Set<ImmediateJobDefinitionCatalogEntity>().Where(item => item.Id == 1)
				.ExecuteUpdateAsync(update => update.SetProperty(item => item.ConcurrencyStamp, stamp), token);
			var definitions = await context.Set<ImmediateJobDefinitionMetadataEntity>()
				.ToDictionaryAsync(static item => item.Name, StringComparer.OrdinalIgnoreCase, token);
			var schedules = await context.Set<ImmediateRecurringJobEntity>()
				.ToDictionaryAsync(static item => item.Name, StringComparer.Ordinal, token);
			var changes = JobDefinitionReconciliation.Create(registration,
				definitions.Values.Select(static item => JsonSerializer.Deserialize(item.Metadata, EntityFrameworkCoreJsonSerializerContext.Default.JobDefinitionRecord)!).ToList(),
				schedules.Values.Select(ToDefinitionSchedule).ToList());
			foreach (var name in changes.RemovedDefinitionNames)
				context.Remove(definitions[name]);
			foreach (var definition in changes.Definitions)
			{
				var metadata = JsonSerializer.Serialize(definition, EntityFrameworkCoreJsonSerializerContext.Default.JobDefinitionRecord);
				if (definitions.TryGetValue(definition.Name, out var current))
					current.Metadata = metadata;
				else
					_ = context.Add(new ImmediateJobDefinitionMetadataEntity { Name = definition.Name, Metadata = metadata });
			}

			foreach (var name in changes.RemovedScheduleNames)
				context.Remove(schedules[name]);
			foreach (var schedule in changes.Schedules)
			{
				if (schedules.TryGetValue(schedule.Name, out var current))
					context.Entry(current).CurrentValues.SetValues(ToEntity(schedule));
				else
					_ = context.Add(ToEntity(schedule));
			}

			_ = await context.SaveChangesAsync(token);
			await transaction.CommitAsync(token);
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		GetJobDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		return await ReadWithStrategyAsync(async (context, token) =>
		{
			var metadata = await context.Set<ImmediateJobDefinitionMetadataEntity>().AsNoTracking()
				.Select(static item => item.Metadata).ToListAsync(token);
			return (IReadOnlyList<JobDefinitionRecord>)[.. metadata
				.Select(static value => JsonSerializer.Deserialize(value, EntityFrameworkCoreJsonSerializerContext.Default.JobDefinitionRecord)!)
				.OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)];
		}, cancellationToken);
	}

	private async Task EnsureDefinitionCatalogAsync(CancellationToken cancellationToken)
	{
		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		if (await context.Set<ImmediateJobDefinitionCatalogEntity>().AnyAsync(item => item.Id == 1, cancellationToken))
			return;
		_ = context.Add(new ImmediateJobDefinitionCatalogEntity { Id = 1, ConcurrencyStamp = Guid.NewGuid() });
		try
		{
			_ = await context.SaveChangesAsync(cancellationToken);
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
		QueueName = entity.QueueName,
		Cron = entity.Cron,
		TimeZone = entity.TimeZone,
		IsCodeDefined = entity.IsCodeDefined,
		IsPaused = entity.IsPaused,
		NextRunAt = entity.NextRunAt,
		LastRunAt = entity.LastRunAt,
	};

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

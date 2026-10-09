using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	/// <inheritdoc />
	public async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
	{
		MergeJobDefinitionsListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		_ = JobDefinitionReconciliation.Create(registration, [], []);
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var version = await Database.StringGetAsync(DefinitionCatalogVersionKey).WaitAsync(cancellationToken);
			var definitions = await GetJobDefinitionsAsync(cancellationToken);
			var schedules = await ReadAllRecurringAsync(cancellationToken);
			var changes = JobDefinitionReconciliation.Create(registration, definitions, schedules);
			var payload = new RedisDefinitionCatalogChanges
			{
				Definitions = changes.Definitions.Select(static definition => new RedisDefinitionMetadata
				{
					Name = definition.Name.ToUpperInvariant(),
					Metadata = JsonSerializer.Serialize(definition, RedisJsonSerializerContext.Default.JobDefinitionRecord),
				}).ToList(),
				RemovedDefinitions = changes.RemovedDefinitionNames.Select(static name => name.ToUpperInvariant()).ToList(),
				RemovedSchedules = changes.RemovedScheduleNames,
				Schedules = changes.Schedules.Select(static schedule => new RedisDefinitionSchedule
				{
					Name = schedule.Name,
					Record = JsonSerializer.Serialize(schedule, RedisJsonSerializerContext.Default.RecurringJobSchedule),
					Cron = schedule.Cron,
					TimeZone = schedule.TimeZone,
					Next = Ticks(schedule.NextRunAt),
					Last = NullableTicks(schedule.LastRunAt),
					DueScore = Score(schedule.NextRunAt),
					DueMember = RecurringDueMember(schedule.NextRunAt, schedule.Name),
					Paused = schedule.IsPaused ? "1" : "0",
				}).ToList(),
			};
			var result = await EvaluateInt64Async(RedisScripts.MergeDefinitionCatalog,
				[DefinitionMetadataKey, DefinitionCatalogVersionKey, RecurringNamesKey, RecurringDueKey],
				[version.IsNull ? "0" : version, _root, JsonSerializer.Serialize(payload, RedisJsonSerializerContext.Default.RedisDefinitionCatalogChanges)],
				cancellationToken);
			if (result == 1)
				return;
			// A competing startup committed. Recompute the entire scoped change from its new snapshot.
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		GetJobDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		var metadata = await Database.HashGetAllAsync(DefinitionMetadataKey).WaitAsync(cancellationToken);
		return [.. metadata.Select(static entry => JsonSerializer.Deserialize((string)entry.Value!, RedisJsonSerializerContext.Default.JobDefinitionRecord)!)
			.OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)];
	}

	[LoggerMessage(
		EventId = LibraryEventIds.MergeJobDefinitionsListAsyncCalled,
		EventName = "Immediate.Jobs.Redis.MergeJobDefinitionsListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MergeJobDefinitionsListAsyncCalled"
	)]
	private partial void MergeJobDefinitionsListAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.Redis.GetJobDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobDefinitionsAsyncCalled"
	)]
	private partial void GetJobDefinitionsAsyncCalled();
}

internal sealed record RedisDefinitionMetadata
{
	public required string Name { get; init; }
	public required string Metadata { get; init; }
}

internal sealed record RedisDefinitionSchedule
{
	public required string Name { get; init; }
	public required string Record { get; init; }
	public required string Cron { get; init; }
	public required string TimeZone { get; init; }
	public required string Next { get; init; }
	public required string Last { get; init; }
	public required long DueScore { get; init; }
	public required string DueMember { get; init; }
	public required string Paused { get; init; }
}

internal sealed record RedisDefinitionCatalogChanges
{
	public required IReadOnlyList<RedisDefinitionMetadata> Definitions { get; init; }
	public required IReadOnlyList<string> RemovedDefinitions { get; init; }
	public required IReadOnlyList<RedisDefinitionSchedule> Schedules { get; init; }
	public required IReadOnlyList<string> RemovedSchedules { get; init; }
}

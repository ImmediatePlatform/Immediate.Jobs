using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	/// <inheritdoc />
	public async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
	{
		MergeJobDefinitionsListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var snapshot = await ReadDefinitionCatalogAsync(cancellationToken);
			var version = (string)snapshot[0]!;
			var definitions = ReadDefinitionRecords(snapshot[1]);
			var schedules = ReadRecurringRecords(snapshot[2]);
			var changes = JobDefinitionReconciliation.Create(registration, definitions, schedules);
			var payload = new RedisDefinitionCatalogChanges
			{
				Definitions = changes.Definitions.Select(static definition => new RedisDefinitionMetadata
				{
					Name = definition.Name.ToUpperInvariant(),
					Metadata = JsonSerializer.Serialize(definition, RedisJsonSerializerContext.Default.JobDefinitionRecord),
				}).ToList(),
				RemovedDefinitions = changes.RemovedDefinitionNames.Select(static name => name.ToUpperInvariant()).ToList(),
				RemovedSchedules = changes.RemovedScheduleNames.Select(NormalizeOrdinalIgnoreCase).ToList(),
				Schedules = changes.Schedules.Select(static schedule => new RedisDefinitionSchedule
				{
					Name = NormalizeOrdinalIgnoreCase(schedule.Name),
					JobName = schedule.JobName.ToUpperInvariant(),
					Record = JsonSerializer.Serialize(schedule, RedisJsonSerializerContext.Default.RecurringJobSchedule),
					Cron = schedule.Cron,
					TimeZone = schedule.TimeZone,
					Next = Timestamp(schedule.NextRunAt),
					Last = NullableTimestamp(schedule.LastRunAt),
					DueScore = Score(schedule.NextRunAt),
					DueMember = RecurringDueMember(schedule.NextRunAt, schedule.Name),
					Paused = schedule.IsPaused ? "1" : "0",
				}).ToList(),
			};
			var now = _timeProvider.GetUtcNow();
			var result = await Database.ScriptEvaluateAsync(RedisScripts.MergeDefinitionCatalog,
				[DefinitionMetadataKey, DefinitionCatalogVersionKey, RecurringNamesKey, RecurringDueKey],
				[version, _root, JsonSerializer.Serialize(payload, RedisJsonSerializerContext.Default.RedisDefinitionCatalogChanges),
					Timestamp(now), Score(now), JobDefinitionReconciliation.RemovalCancellationReason]).WaitAsync(cancellationToken);
			if (!result.IsNull)
			{
				foreach (var name in changes.RemovedDefinitionNames)
					_definitionSnapshots.TryRemove(name.ToUpperInvariant(), out _);
				for (var index = 0; index < changes.Definitions.Count; index++)
				{
					var metadata = payload.Definitions[index];
					_definitionSnapshots[metadata.Name] = new(metadata.Metadata, changes.Definitions[index]);
				}

				foreach (var report in (RedisResult[])result!)
				{
					var values = (RedisResult[])report!;
					DefinitionRemoved((string)values[0]!, (int)(long)values[1], (int)(long)values[2]);
				}

				return;
			}
			// A competing startup committed. Recompute the entire scoped change from its new snapshot.
		}
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		GetJobDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		var metadata = await Database.HashValuesAsync(DefinitionMetadataKey).WaitAsync(cancellationToken);
		return ReadDefinitionRecords(metadata);
	}

	private async Task<RedisResult[]> ReadDefinitionCatalogAsync(CancellationToken cancellationToken) =>
		(RedisResult[])(await Database.ScriptEvaluateAsync(
			RedisScripts.ReadDefinitionCatalog, [DefinitionCatalogVersionKey, DefinitionMetadataKey, RecurringNamesKey], [_root]
		).WaitAsync(cancellationToken))!;

	private static IReadOnlyList<JobDefinitionRecord> ReadDefinitionRecords(RedisResult result) =>
		ReadDefinitionRecords(ReadValues(result));

	private static IReadOnlyList<JobDefinitionRecord> ReadDefinitionRecords(RedisValue[] metadata) =>
		[.. metadata.Select(static value => JsonSerializer.Deserialize(
			(byte[])value!, RedisJsonSerializerContext.Default.JobDefinitionRecord)!)
			.OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)];

	[LoggerMessage(
		EventId = LibraryEventIds.DefinitionRemoved,
		EventName = "Immediate.Jobs.Redis.DefinitionRemoved",
		Level = LogLevel.Warning,
		Message = "Job definition '{JobName}' removed; canceled {JobCount} invocations and {ScheduleCount} recurring schedules."
	)]
	private partial void DefinitionRemoved(string jobName, int jobCount, int scheduleCount);

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
	public required string JobName { get; init; }
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

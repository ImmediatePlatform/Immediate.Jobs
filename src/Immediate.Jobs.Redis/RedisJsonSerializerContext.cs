using System.Text.Json;
using System.Text.Json.Serialization;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;

namespace Immediate.Jobs.Redis;

[JsonSerializable(typeof(JobSubmission))]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(RecurringJobSchedule))]
[JsonSerializable(typeof(JobServerSnapshot))]
[JsonSerializable(typeof(JobDefinition))]
[JsonSerializable(typeof(RedisDefinitionCatalogChanges))]
internal sealed partial class RedisJsonSerializerContext : JsonSerializerContext
{
	internal static JobDefinition? DeserializeJobDefinition(string json)
	{
		using var document = JsonDocument.Parse(json);
		var definition = document.RootElement.Deserialize(Default.JobDefinition);
		if (definition is null || document.RootElement.TryGetProperty("Queue", out _))
			return definition;

		// Read catalogues written before definitions stored their queue as a nested object.
		return document.RootElement.TryGetProperty("QueueName", out var name)
			? definition with
			{
				Queue = new()
				{
					Name = name.GetString()!,
					Priority = document.RootElement.TryGetProperty("QueuePriority", out var priority) ? priority.GetInt32() : 0,
					Concurrency = document.RootElement.TryGetProperty("QueueConcurrency", out var concurrency) ? concurrency.GetInt32() : 0,
				},
			}
			: definition;
	}
}

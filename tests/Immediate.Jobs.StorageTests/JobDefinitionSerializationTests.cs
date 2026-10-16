using System.Text.Json;
using Immediate.Jobs.Redis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;

namespace Immediate.Jobs.StorageTests;

public sealed class JobDefinitionSerializationTests
{
	[Fact]
	public void LocalDefinitionsRoundTripWithoutRuntimeBindings()
	{
		var definition = new LocalDefinition
		{
			Name = "configured",
			Tags = ["email"],
			Queue = new() { Name = "priority", Priority = 9, Concurrency = 2 },
			Invoker = new NoOpInvoker(),
			JobType = typeof(JobDefinitionSerializationTests),
			MaxConcurrency = 3,
			SlidingWindowMax = 5,
			SlidingWindowPeriod = TimeSpan.FromSeconds(17),
		};
		var metadata = RedisJsonSerializerContext.Default.JobDefinition;
		var json = JsonSerializer.Serialize(definition, metadata);
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		Assert.Equal("priority", root.GetProperty("QueueName").GetString());
		Assert.Equal(9, root.GetProperty("QueuePriority").GetInt32());
		Assert.Equal(2, root.GetProperty("QueueConcurrency").GetInt32());
		Assert.False(root.TryGetProperty("Queue", out _));
		Assert.False(root.TryGetProperty("Invoker", out _));
		Assert.False(root.TryGetProperty("JobType", out _));
		Assert.False(root.TryGetProperty("AcquisitionLimits", out _));

		var stored = Assert.IsType<JobDefinition>(JsonSerializer.Deserialize(json, metadata));
		Assert.Equal(definition.Queue, stored.Queue);
		Assert.Equal(definition.AcquisitionLimits, stored.AcquisitionLimits);
		Assert.Equal(definition.Tags, stored.Tags);
		Assert.Null(stored.Invoker);
		Assert.Null(stored.JobType);
		Assert.Equal(json, JsonSerializer.Serialize(stored, metadata));
	}

	[Fact]
	public void ExistingFlatMetadataRestoresQueueSettings()
	{
		const string Json = """
			{
			  "Name": "existing",
			  "QueueConcurrency": 2,
			  "QueuePriority": 9,
			  "QueueName": "priority",
			  "Tags": [],
			  "Cron": null,
			  "TimeZone": "UTC",
			  "MaxAttempts": 3,
			  "Timeout": null,
			  "MaxConcurrency": 0,
			  "SlidingWindowMax": 0,
			  "SlidingWindowPeriod": null,
			  "FixedWindowMax": 0,
			  "FixedWindowPeriod": null,
			  "OverlapPolicy": 0,
			  "MisfireHandlingMode": 1,
			  "Backoff": 2,
			  "BackoffBase": "00:00:05"
			}
			""";
		var definition = Assert.IsType<JobDefinition>(JsonSerializer.Deserialize(Json, RedisJsonSerializerContext.Default.JobDefinition));
		Assert.Equal(new() { Name = "priority", Priority = 9, Concurrency = 2 }, definition.Queue);
		Assert.Equal([JobTags.Default], definition.Tags);
		Assert.Equal(3, definition.MaxAttempts);
		Assert.Equal("UTC", definition.TimeZone);
		Assert.Null(definition.Invoker);
		Assert.Null(definition.JobType);

		var updated = definition with { QueueName = "changed", QueueConcurrency = 4 };
		Assert.Equal(new() { Name = "changed", Priority = 9, Concurrency = 4 }, updated.Queue);
		Assert.Equal("priority", definition.QueueName);
		Assert.Equal(2, definition.QueueConcurrency);
	}

	private sealed record LocalDefinition : JobDefinition;

	private sealed class NoOpInvoker : IJobInvoker
	{
		public ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution) => ValueTask.CompletedTask;
	}
}

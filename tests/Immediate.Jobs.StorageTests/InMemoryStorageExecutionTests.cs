using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class InMemoryStorageExecutionTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LegacyExecutionSurvivesTelemetryFailureAndRetry(bool recordLegacyTelemetry)
	{
		var token = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
		var now = clock.GetUtcNow();
		await using var storage = new InMemoryJobStorage(clock);
		await storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = "Work" }],
			RecurringSchedules = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
		}, token);
		var handle = JobHandle.FromString("legacy");
		storage.LoadPersistedJobState([new()
		{
			JobHandle = handle,
			JobName = "Work",
			Payload = "{}",
			State = JobState.Active,
			CreatedAt = now,
			DueAt = now,
			Attempt = 3,
			WorkerId = "worker",
			LeaseExpiresAt = now.AddMinutes(1),
		}], [], [], []);
		var original = Assert.Single(await storage.QueryJobExecutionsAsync(handle, new(), token));
		if (recordLegacyTelemetry)
			await storage.SetExecutionTelemetryAsync(handle, 3, "worker", "legacy-trace", "legacy-span", now, token);

		clock.Advance(TimeSpan.FromSeconds(1));
		await storage.FailAsync(handle, 3, "worker", "legacy failure", nextRetryAt: null, token);
		var failed = Assert.Single(await storage.QueryJobExecutionsAsync(handle, new(), token));
		Assert.True(failed.IsSynthetic);
		Assert.Equal(JobExecutionState.Failed, failed.State);
		Assert.Equal("legacy failure", failed.Error);
		Assert.Equal(clock.GetUtcNow(), failed.CompletedAt);
		Assert.Equal(recordLegacyTelemetry ? "legacy-trace" : null, failed.ExecutionTraceId);
		Assert.Equal(recordLegacyTelemetry ? "legacy-span" : null, failed.ExecutionSpanId);
		Assert.Null(failed.AcquiredAt);
		Assert.Equal(JobExecutionState.Active, original.State);
		Assert.Null(original.ExecutionTraceId);

		await storage.RetryAsync(handle, token);
		var active = Assert.Single(await storage.AcquireDueJobsAsync(new()
		{
			WorkerId = "worker",
			Lease = TimeSpan.FromMinutes(1),
			BatchSize = 1,
			Queues = [new() { QueueName = JobQueueDefinition.DefaultName, Capacity = 1, JobCapacities = new Dictionary<string, int> { ["Work"] = 1 } }],
		}, token));
		Assert.Equal(4, active.Attempt);
		await storage.SetExecutionTelemetryAsync(handle, 4, "worker", "new-trace", "new-span", clock.GetUtcNow(), token);
		clock.Advance(TimeSpan.FromSeconds(1));
		await storage.CompleteAsync(handle, 4, "worker", token);

		var history = await storage.QueryJobExecutionsAsync(handle, new(), token);
		Assert.Equal([4, 3], history.Select(static execution => execution.Attempt));
		Assert.False(history[0].IsSynthetic);
		Assert.Equal(JobExecutionState.Succeeded, history[0].State);
		Assert.Equal("new-trace", history[0].ExecutionTraceId);
		Assert.Equal("new-span", history[0].ExecutionSpanId);
		Assert.Equal(now.AddSeconds(1), history[0].AcquiredAt);
		Assert.Equal(clock.GetUtcNow(), history[0].CompletedAt);
		Assert.Equal(failed, history[1]);
		Assert.Equal(failed, Assert.Single(await storage.QueryJobExecutionsAsync(handle, new() { Skip = 1, Take = 1 }, token)));
		Assert.Equal(history[0], Assert.Single(await storage.QueryJobExecutionsAsync(handle, new() { Attempt = 4 }, token)));
	}
}

using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class JobExecutionStorageTests
{
	[Fact]
	public async Task LegacyLatestExecutionIsMarkedSyntheticAndMaterializedBeforeRetryClearsIt()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var completedAt = clock.GetUtcNow().AddMinutes(-1);
		await storage.EnqueueAsync(CreateJob("legacy", clock.GetUtcNow()) with
		{
			State = JobState.Failed,
			Attempt = 4,
			CompletedAt = completedAt,
			LastError = "legacy failure",
			ExecutionStartedAt = completedAt.AddSeconds(-2),
			ExecutionTraceId = "44444444444444444444444444444444",
			ExecutionSpanId = "4444444444444444",
		}, cancellationToken);

		var synthetic = Assert.Single(await storage.QueryJobExecutionsAsync(new() { JobId = "legacy" }, cancellationToken));
		Assert.True(synthetic.IsSynthetic);
		Assert.Equal(4, synthetic.Attempt);
		Assert.Equal(JobExecutionState.Failed, synthetic.State);
		Assert.Equal("legacy failure", synthetic.Error);
		Assert.Equal(completedAt, synthetic.CompletedAt);

		await storage.RetryAsync("legacy", cancellationToken);
		var retained = Assert.Single(await storage.QueryJobExecutionsAsync(new() { JobId = "legacy" }, cancellationToken));
		Assert.Equal(synthetic, retained);
	}

	[Fact]
	public async Task ExecutionQueryValidatesEveryFilter()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(TimeProvider.System);

		_ = await Assert.ThrowsAsync<ArgumentException>(
			() => storage.QueryJobExecutionsAsync(new() { JobId = "" }, cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => storage.QueryJobExecutionsAsync(new() { JobId = "job", Attempt = 0 }, cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => storage.QueryJobExecutionsAsync(new() { JobId = "job", Skip = -1 }, cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => storage.QueryJobExecutionsAsync(new() { JobId = "job", Take = 0 }, cancellationToken).AsTask()
		);
	}

	private static JobRecord CreateJob(string id, DateTimeOffset now) => new()
	{
		Id = id,
		JobName = "execution-test",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now,
	};

}

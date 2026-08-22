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
		var now = clock.GetUtcNow();
		var completedAt = now.AddMinutes(-1);
		JobHandle jobId = new() { JobId = "legacy" };
		await storage.EnqueueAsync(CreateJob(jobId, now.AddMinutes(-2)) with
		{
			State = JobState.Failed,
			Attempt = 4,
			CompletedAt = completedAt,
			LastError = "legacy failure",
			ExecutionStartedAt = completedAt.AddSeconds(-2),
			ExecutionTraceId = "44444444444444444444444444444444",
			ExecutionSpanId = "4444444444444444",
		}, cancellationToken);

		var synthetic = Assert.Single(await storage.QueryJobExecutionsAsync(jobId, new() { }, cancellationToken));
		Assert.True(synthetic.IsSynthetic);
		Assert.Equal(4, synthetic.Attempt);
		Assert.Equal(JobExecutionState.Failed, synthetic.State);
		Assert.Equal("legacy failure", synthetic.Error);
		Assert.Equal(completedAt, synthetic.CompletedAt);

		await storage.RetryAsync(jobId, cancellationToken);
		var retained = Assert.Single(await storage.QueryJobExecutionsAsync(jobId, new() { }, cancellationToken));
		Assert.Equal(synthetic, retained);
	}

	private static JobRecord CreateJob(JobHandle id, DateTimeOffset now) => new()
	{
		JobId = id,
		JobName = "execution-test",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now,
	};

}

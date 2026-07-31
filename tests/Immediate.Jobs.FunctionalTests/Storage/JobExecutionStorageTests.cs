using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

#pragma warning disable CS1591
public sealed class JobExecutionStorageTests
{
	[Fact]
	public async Task FailedExecutionIsRetainedAndAttemptFencesAReacquisitionByTheSameWorker()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.EnqueueAsync(CreateJob("retried", clock.GetUtcNow()), cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		Assert.Equal(1, first.Attempt);
		var firstStartedAt = clock.GetUtcNow().AddSeconds(1);
		await storage.SetExecutionTelemetryAsync(
			first.Id,
			first.Attempt,
			"worker",
			"11111111111111111111111111111111",
			"1111111111111111",
			firstStartedAt,
			cancellationToken
		);
		clock.Advance(TimeSpan.FromSeconds(2));
		const string FirstError = "System.InvalidOperationException: first execution failed\n   at Test.Handler()";
		await storage.FailAsync(
			first.Id,
			first.Attempt,
			"worker",
			FirstError,
			clock.GetUtcNow(),
			cancellationToken
		);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		Assert.Equal(2, second.Attempt);
		var secondStartedAt = clock.GetUtcNow().AddSeconds(1);
		await storage.SetExecutionTelemetryAsync(
			second.Id,
			second.Attempt,
			"worker",
			"22222222222222222222222222222222",
			"2222222222222222",
			secondStartedAt,
			cancellationToken
		);

		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.RenewLeaseAsync(
				second.Id,
				first.Attempt,
				"worker",
				TimeSpan.FromMinutes(1),
				cancellationToken
			).AsTask()
		);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.SetExecutionTelemetryAsync(
			second.Id,
			first.Attempt,
			"worker",
			"stale",
			"stale",
			clock.GetUtcNow(),
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.CompleteAsync(second.Id, first.Attempt, "worker", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.FailAsync(
			second.Id,
			first.Attempt,
			"worker",
			"stale",
			nextRetryAt: null,
			cancellationToken
		).AsTask());

		clock.Advance(TimeSpan.FromSeconds(2));
		await storage.CompleteAsync(second.Id, second.Attempt, "worker", cancellationToken);

		var executions = await storage.QueryJobExecutionsAsync(new() { JobId = second.Id }, cancellationToken);
		Assert.Collection(
			executions,
			execution =>
			{
				Assert.Equal(2, execution.Attempt);
				Assert.Equal(JobExecutionState.Succeeded, execution.State);
				Assert.Equal("22222222222222222222222222222222", execution.ExecutionTraceId);
				Assert.Equal("2222222222222222", execution.ExecutionSpanId);
				Assert.Equal(secondStartedAt, execution.ExecutionStartedAt);
				Assert.Null(execution.Error);
				Assert.False(execution.IsSynthetic);
			},
			execution =>
			{
				Assert.Equal(1, execution.Attempt);
				Assert.Equal(JobExecutionState.Failed, execution.State);
				Assert.Equal("11111111111111111111111111111111", execution.ExecutionTraceId);
				Assert.Equal("1111111111111111", execution.ExecutionSpanId);
				Assert.Equal(firstStartedAt, execution.ExecutionStartedAt);
				Assert.Equal(FirstError, execution.Error);
				Assert.False(execution.IsSynthetic);
			}
		);
	}

	[Fact]
	public async Task ExpiredLeaseIsClosedAsInterruptedBeforeTheReplacementExecutionBegins()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.EnqueueAsync(CreateJob("interrupted", clock.GetUtcNow()), cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		var expectedCompletedAt = first.LeaseExpiresAt;
		clock.Advance(TimeSpan.FromMinutes(2));
		var second = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));

		var executions = await storage.QueryJobExecutionsAsync(new() { JobId = first.Id }, cancellationToken);
		Assert.Equal(2, second.Attempt);
		Assert.Collection(
			executions,
			execution =>
			{
				Assert.Equal(2, execution.Attempt);
				Assert.Equal(JobExecutionState.Active, execution.State);
			},
			execution =>
			{
				Assert.Equal(1, execution.Attempt);
				Assert.Equal(JobExecutionState.Interrupted, execution.State);
				Assert.Equal(expectedCompletedAt, execution.CompletedAt);
			}
		);
	}

	[Fact]
	public async Task ManualRetryCreatesNoExecutionUntilAcquisitionAndSupportsExactPaging()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		await storage.EnqueueAsync(CreateJob("manual-retry", clock.GetUtcNow()), cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		await storage.FailAsync(first.Id, first.Attempt, "worker", "failed", nextRetryAt: null, cancellationToken);
		await storage.RetryAsync(first.Id, cancellationToken);
		var beforeAcquisition = await storage.QueryJobExecutionsAsync(new() { JobId = first.Id }, cancellationToken);
		Assert.Equal(JobExecutionState.Failed, Assert.Single(beforeAcquisition).State);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		await storage.CompleteAsync(second.Id, second.Attempt, "worker", cancellationToken);
		var newestPage = await storage.QueryJobExecutionsAsync(new()
		{
			JobId = first.Id,
			Take = 1,
		}, cancellationToken);
		var olderPage = await storage.QueryJobExecutionsAsync(new()
		{
			JobId = first.Id,
			Skip = 1,
			Take = 1,
		}, cancellationToken);
		var exact = await storage.QueryJobExecutionsAsync(new()
		{
			JobId = first.Id,
			Attempt = 1,
		}, cancellationToken);

		Assert.Equal(2, Assert.Single(newestPage).Attempt);
		Assert.Equal(1, Assert.Single(olderPage).Attempt);
		Assert.Equal(1, Assert.Single(exact).Attempt);
	}

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

	private static JobAcquisitionRequest CreateRequest(string workerId) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = 1,
		Queues =
		[
			new()
			{
				QueueName = JobQueueDefinition.DefaultName,
				Capacity = 1,
				JobCapacities = new Dictionary<string, int> { ["execution-test"] = 1 },
			},
		],
	};
}
#pragma warning restore CS1591

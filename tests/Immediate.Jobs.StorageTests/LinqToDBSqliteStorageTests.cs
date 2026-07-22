using Immediate.Jobs.LinqToDB;
using LinqToDB;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

#pragma warning disable CS1591
public sealed class LinqToDBSqliteStorageTests
{
	[Fact]
	public async Task SchemaBootstrapIsIdempotentAndRejectsNamedSqliteSchema()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		await fixture.Options.CreateImmediateJobsSchemaAsync(cancellationToken: cancellationToken);
		await fixture.Options.CreateImmediateJobsSchemaAsync(cancellationToken: cancellationToken);
		var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			fixture.Options.CreateImmediateJobsSchemaAsync("jobs", cancellationToken)
		);
		Assert.Equal("schema", exception.ParamName);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("{\"usage\":{\"userId\":\"42\"}}")]
	public async Task ContextAndLeaseRecoveryRoundTrip(string? context)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with { Context = context };
		await storage.EnqueueAsync(job, cancellationToken);

		var queried = Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken));
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		Assert.Equal(context, queried.Context);
		Assert.Equal(context, acquired.Context);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
		var recovered = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-b", recovered.WorkerId);
	}

	[Theory]
	[InlineData(ContinuationTrigger.Success, true, JobState.Pending)]
	[InlineData(ContinuationTrigger.Success, false, JobState.Cancelled)]
	[InlineData(ContinuationTrigger.Failure, true, JobState.Cancelled)]
	[InlineData(ContinuationTrigger.Failure, false, JobState.Pending)]
	[InlineData(ContinuationTrigger.Complete, true, JobState.Pending)]
	[InlineData(ContinuationTrigger.Complete, false, JobState.Pending)]
	public async Task ContinuationTriggersMatchParentOutcome(
		ContinuationTrigger trigger,
		bool parentSucceeds,
		JobState expectedState
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob(now, 1) with { Id = "parent" };
		var child = CreateJob(now, 2) with
		{
			Id = "child",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueAsync(parent, cancellationToken);
		await storage.EnqueueContinuationAsync(child, [new()
		{
			ChildJobId = child.Id,
			ParentJobId = parent.Id,
			Trigger = trigger,
		}], cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		if (parentSucceeds)
			await storage.CompleteAsync(parent.Id, "worker", cancellationToken);
		else
			await storage.FailAsync(parent.Id, "worker", "expected", nextRetryAt: null, cancellationToken);
		Assert.Equal(expectedState, (await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State);
	}

	[Fact]
	public async Task BatchCountersAndCancellationRemainAtomic()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob(now, 1) with { Id = "batch-parent", BatchId = "batch" };
		var child = CreateJob(now, 2) with
		{
			Id = "batch-child",
			BatchId = "batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(new()
		{
			Id = "batch",
			CreatedAt = now,
			TotalJobs = 2,
			PendingCount = 2,
			State = BatchState.Executing,
		}, [parent, child], [new() { ChildJobId = child.Id, ParentJobId = parent.Id }], cancellationToken);

		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		await storage.CompleteAsync(parent.Id, "worker", cancellationToken);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State);
		await storage.CancelBatchAsync("batch", cancellationToken);
		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(BatchState.Cancelled, batch.State);
		Assert.Equal(1, batch.Succeeded);
		Assert.Equal(1, batch.Cancelled);
		Assert.Equal(0, batch.Remaining);
	}

	private static JobAcquisitionRequest CreateRequest(string workerId, int batchSize) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = batchSize,
		Queues = [new()
		{
			QueueName = JobQueueDefinition.DefaultName,
			Capacity = batchSize,
			JobCapacities = new Dictionary<string, int> { ["storage-test"] = batchSize },
		}],
	};

	private static JobRecord CreateJob(DateTimeOffset now, int index) => new()
	{
		Id = "job-" + Guid.NewGuid().ToString("N"),
		JobName = "storage-test",
		Payload = $"{{\"index\":{index}}}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now.AddTicks(index),
	};

	private sealed class StorageFixture(string databasePath, DataOptions options) : IAsyncDisposable
	{
		public DataOptions Options { get; } = options;
		public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero));

		public LinqToDBJobStorage CreateStorage() => new(Options, timeProvider: TimeProvider);

		public static async Task<StorageFixture> CreateAsync(CancellationToken cancellationToken)
		{
			var path = Path.Combine(Path.GetTempPath(), $"immediate-jobs-{Guid.NewGuid():N}.db");
			var options = new DataOptions().UseSQLite($"Data Source={path}");
			var fixture = new StorageFixture(path, options);
			await options.CreateImmediateJobsSchemaAsync(cancellationToken: cancellationToken);
			return fixture;
		}

		public ValueTask DisposeAsync()
		{
			File.Delete(databasePath);
			return ValueTask.CompletedTask;
		}
	}
}
#pragma warning restore CS1591

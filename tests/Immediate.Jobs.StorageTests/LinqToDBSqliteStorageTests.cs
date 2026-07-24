using Immediate.Jobs.LinqToDB;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
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
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with
		{
			Context = context,
			GroupId = "tenant-a",
		};
		await storage.EnqueueAsync(job, cancellationToken);

		var queried = Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken));
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		Assert.Equal(context, queried.Context);
		Assert.Equal(context, acquired.Context);
		Assert.Equal("tenant-a", queried.GroupId);
		Assert.Equal("tenant-a", acquired.GroupId);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
		var recovered = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-b", recovered.WorkerId);
		Assert.Equal("tenant-a", recovered.GroupId);
	}

	[Fact]
	public async Task FairQueuesRotateGroupsWhenCapacityIsOneAndTheSecondGroupArrivesLater()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with
		{
			Id = "group-a-first",
			GroupId = "group-a",
		}, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with
		{
			Id = "group-a-second",
			GroupId = "group-a",
		}, cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-a", 1),
			cancellationToken
		));
		await storage.CompleteAsync(first.Id, "worker-a", cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with
		{
			Id = "group-b-first",
			GroupId = "group-b",
		}, cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-b", 1),
			cancellationToken
		));

		Assert.Equal("group-a-first", first.Id);
		Assert.Equal("group-b-first", second.Id);
	}

	[Fact]
	public async Task FairQueuesInterleaveGroupsWithinOneBatch()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-1", 0, "a", cancellationToken);
		await Enqueue(storage, now, "a-2", 1, "a", cancellationToken);
		await Enqueue(storage, now, "b-1", 2, "b", cancellationToken);
		await Enqueue(storage, now, "b-2", 3, "b", cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(CreateFairRequest("worker", 4), cancellationToken);

		Assert.Equal(["a-1", "b-1", "a-2", "b-2"], acquired.Select(static job => job.Id));
	}

	[Fact]
	public async Task DisabledFairQueuesRetainExistingOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-1", 0, "a", cancellationToken);
		await Enqueue(storage, now, "a-2", 1, "a", cancellationToken);
		await Enqueue(storage, now, "b-1", 2, "b", cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(CreateRequest("worker", 2), cancellationToken);

		Assert.Equal(["a-1", "a-2"], acquired.Select(static job => job.Id));
	}

	[Fact]
	public async Task EnabledFairQueuesWithoutGroupedJobsRetainExistingOrder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "newer", 2, groupId: null, cancellationToken);
		await Enqueue(storage, now, "oldest", 0, groupId: null, cancellationToken);
		await Enqueue(storage, now, "middle", 1, groupId: null, cancellationToken);

		var acquired = await storage.AcquireDueJobsAsync(CreateFairRequest("worker", 3), cancellationToken);

		Assert.Equal(["oldest", "middle", "newer"], acquired.Select(static job => job.Id));
	}

	[Fact]
	public async Task NoisyGroupIsServedAfterQuietGroup()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "noisy-active-1", 0, "noisy", cancellationToken);
		await Enqueue(storage, now, "noisy-active-2", 1, "noisy", cancellationToken);
		_ = await storage.AcquireJobsAsync(
			["noisy-active-1", "noisy-active-2"],
			"existing-worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		);
		await Enqueue(storage, now, "noisy-waiting", 2, "noisy", cancellationToken);
		await Enqueue(storage, now, "quiet-waiting", 3, "quiet", cancellationToken);

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker", 1, new(0.50, 2, true)),
			cancellationToken
		));

		Assert.Equal("quiet-waiting", acquired.Id);
	}

	[Fact]
	public async Task ExpiredLeasesDoNotMakeAGroupNoisy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "expired-1", 0, "formerly-noisy", cancellationToken, "inactive");
		await Enqueue(storage, now, "expired-2", 1, "formerly-noisy", cancellationToken, "inactive");
		_ = await storage.AcquireJobsAsync(
			["expired-1", "expired-2"],
			"expired-worker",
			TimeSpan.FromSeconds(1),
			cancellationToken
		);
		await Enqueue(storage, now, "formerly-noisy", 2, "formerly-noisy", cancellationToken);
		await Enqueue(storage, now, "quiet", 3, "quiet", cancellationToken);
		fixture.TimeProvider.Advance(TimeSpan.FromSeconds(2));

		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker", 1, new(0.50, 2, true)),
			cancellationToken
		));

		Assert.Equal("formerly-noisy", acquired.Id);
	}

	[Fact]
	public async Task DisablingRoundRobinUsesDueOrderAfterNoisyClassification()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-1", 0, "a", cancellationToken);
		await Enqueue(storage, now, "a-2", 1, "a", cancellationToken);
		await Enqueue(storage, now, "b-1", 2, "b", cancellationToken);
		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-a", 1),
			cancellationToken
		));
		await storage.CompleteAsync(first.Id, "worker-a", cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-b", 1, new(0.10, 30, false)),
			cancellationToken
		));

		Assert.Equal("a-2", second.Id);
	}

	[Fact]
	public async Task FairAcquisitionPreservesUngroupedOrderAndCapacities()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "ungrouped-limited", 0, groupId: null, cancellationToken, "limited");
		await Enqueue(storage, now, "grouped-limited", 1, "a", cancellationToken, "limited");
		await Enqueue(storage, now, "grouped-other-1", 2, "b", cancellationToken, "other");
		await Enqueue(storage, now, "grouped-other-2", 3, "c", cancellationToken, "other");
		var request = CreateFairRequest("worker", 4) with
		{
			Queues =
			[
				new()
				{
					QueueName = JobQueueDefinition.DefaultName,
					Capacity = 2,
					JobCapacities = new Dictionary<string, int> { ["limited"] = 1, ["other"] = 10 },
				},
			],
		};

		var acquired = await storage.AcquireDueJobsAsync(request, cancellationToken);

		Assert.Equal(["ungrouped-limited", "grouped-other-1"], acquired.Select(static job => job.Id));
	}

	[Fact]
	public async Task CursorResetsAfterGroupBacklogClears()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-first", 0, "a", cancellationToken);
		await Enqueue(storage, now, "a-second", 1, "a", cancellationToken);
		await Enqueue(storage, now, "b-active", 2, "b", cancellationToken);
		await Enqueue(storage, now, "b-waiting", 3, "b", cancellationToken);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-1", 1),
			cancellationToken
		));
		var second = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-2", 1),
			cancellationToken
		));
		await storage.CompleteAsync(first.Id, "worker-1", cancellationToken);
		var third = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-3", 1),
			cancellationToken
		));
		await storage.CompleteAsync(third.Id, "worker-3", cancellationToken);
		await Enqueue(storage, now, "a-returned", 4, "a", cancellationToken);

		var afterReset = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker-4", 1),
			cancellationToken
		));

		Assert.Equal("a-first", first.Id);
		Assert.Equal("b-active", second.Id);
		Assert.Equal("a-second", third.Id);
		Assert.Equal("a-returned", afterReset.Id);
	}

	[Fact]
	public async Task ConcurrentFairClaimsDoNotDuplicateJobs()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		foreach (var index in Enumerable.Range(0, 12))
			await Enqueue(first, now, $"job-{index}", index, $"group-{index % 3}", cancellationToken);

		var claims = await Task.WhenAll(
			first.AcquireDueJobsAsync(CreateFairRequest("worker-a", 12), cancellationToken).AsTask(),
			second.AcquireDueJobsAsync(CreateFairRequest("worker-b", 12), cancellationToken).AsTask()
		);
		var acquired = claims.SelectMany(static jobs => jobs).ToArray();

		Assert.Equal(12, acquired.Length);
		Assert.Equal(12, acquired.Select(static job => job.Id).Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task DuplicateCursorSequencesConvergeWithoutStarvingAnUnservedGroup()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "a-first", 0, "a", cancellationToken);
		await Enqueue(storage, now, "b-first", 1, "b", cancellationToken);
		await Enqueue(storage, now, "a-next", 2, "a", cancellationToken);
		await Enqueue(storage, now, "b-next", 3, "b", cancellationToken);

		var firstPass = await storage.AcquireDueJobsAsync(CreateFairRequest("worker-1", 2), cancellationToken);
		Assert.Equal(["a-first", "b-first"], firstPass.Select(static job => job.Id));
		foreach (var job in firstPass)
			await storage.CompleteAsync(job.Id, "worker-1", cancellationToken);

		await using (var connection = new DataConnection(fixture.Options))
		{
			var updated = await connection.ExecuteAsync(
				"""
				UPDATE "immediate_fair_queue_groups"
				SET "LastServedSequence" = 5
				WHERE "QueueName" = 'default'
				""",
				cancellationToken
			);
			Assert.Equal(2, updated);
		}

		await Enqueue(storage, now, "c-first", 4, "c", cancellationToken);

		var secondPass = await storage.AcquireDueJobsAsync(CreateFairRequest("worker-2", 3), cancellationToken);

		Assert.Equal(["c-first", "a-next", "b-next"], secondPass.Select(static job => job.Id));
	}

	[Fact]
	public async Task CursorCleanupFailureDoesNotRollBackACompletedJob()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await Enqueue(storage, now, "grouped", 0, "a", cancellationToken);
		var acquired = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateFairRequest("worker", 1),
			cancellationToken
		));

		await using (var connection = new DataConnection(fixture.Options))
			_ = await connection.ExecuteAsync("DROP TABLE \"immediate_fair_queue_groups\"", cancellationToken);

		await storage.CompleteAsync(acquired.Id, "worker", cancellationToken);

		var completed = Assert.Single(await storage.QueryJobsAsync(new() { Id = acquired.Id }, cancellationToken));
		Assert.Equal(JobState.Succeeded, completed.State);
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

	private static JobAcquisitionRequest CreateFairRequest(
		string workerId,
		int batchSize,
		FairQueuePolicy? policy = null
	) =>
		CreateRequest(workerId, batchSize) with
		{
			FairQueues = policy ?? new(0.10, 30, true),
		};

	private static ValueTask Enqueue(
		LinqToDBJobStorage storage,
		DateTimeOffset now,
		string id,
		int order,
		string? groupId,
		CancellationToken cancellationToken,
		string jobName = "storage-test"
	) => storage.EnqueueAsync(CreateJob(now, order) with
	{
		Id = id,
		JobName = jobName,
		GroupId = groupId,
	}, cancellationToken);

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
		private readonly List<LinqToDBJobStorage> _storages = [];

		public DataOptions Options { get; } = options;
		public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero));

		public LinqToDBJobStorage CreateStorage()
		{
			var storage = new LinqToDBJobStorage(Options, timeProvider: TimeProvider);
			_storages.Add(storage);
			return storage;
		}

		public static async Task<StorageFixture> CreateAsync(CancellationToken cancellationToken)
		{
			var path = Path.Combine(Path.GetTempPath(), $"immediate-jobs-{Guid.NewGuid():N}.db");
			var options = new DataOptions().UseSQLite($"Data Source={path}");
			var fixture = new StorageFixture(path, options);
			try
			{
				await options.CreateImmediateJobsSchemaAsync(cancellationToken: cancellationToken);
				return fixture;
			}
			catch
			{
				await fixture.DisposeAsync();
				throw;
			}
		}

		public async ValueTask DisposeAsync()
		{
			foreach (var storage in _storages.AsEnumerable().Reverse())
				await storage.DisposeAsync();
			SqliteConnection.ClearAllPools();
			File.Delete(databasePath);
		}
	}
}
#pragma warning restore CS1591

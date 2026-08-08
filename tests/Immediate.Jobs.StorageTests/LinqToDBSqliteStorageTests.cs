using System.Globalization;
using Immediate.Jobs.LinqToDB;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
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

	[Fact]
	public async Task EmptyBatchIsFullySettled()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		await using (var connection = new DataConnection(fixture.Options))
		{
			_ = await connection.ExecuteAsync(
				$"""
				INSERT INTO "immediate_job_batches"
				("Id", "CreatedAt", "TotalJobs", "PendingCount", "SucceededCount", "FailedCount", "CancelledCount", "SkippedCount", "State", "ConcurrencyStamp")
				VALUES ('empty', 0, 0, 0, 0, 0, 0, 0, {(int)BatchState.Succeeded}, '{Guid.NewGuid()}')
				""",
				cancellationToken
			);
		}

		var status = Assert.IsType<BatchStatus>(await fixture.CreateStorage().GetBatchStatusAsync("empty", cancellationToken));

		Assert.Equal(1d, status.FractionSettled);
	}

	[Fact]
	public async Task ReturningGroupCursorResetCommitsWithGroupedEnqueue()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		await InsertCursorAsync(fixture.Options, "returning", 42, cancellationToken);

		await fixture.CreateStorage().EnqueueAsync(CreateJob(fixture.TimeProvider.GetUtcNow(), 0) with
		{
			GroupId = "returning",
		}, cancellationToken);

		Assert.Equal(0, await DeleteCursorAsync(fixture.Options, "returning", cancellationToken));
	}

	[Fact]
	public async Task FailedGroupedEnqueueRollsBackReturningGroupCursorReset()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var duplicate = CreateJob(fixture.TimeProvider.GetUtcNow(), 0) with
		{
			Id = "duplicate",
			GroupId = "other",
		};
		await storage.EnqueueAsync(duplicate, cancellationToken);
		await InsertCursorAsync(fixture.Options, "returning", 42, cancellationToken);

		_ = await Assert.ThrowsAnyAsync<Exception>(() =>
			storage.EnqueueAsync(duplicate with { GroupId = "returning" }, cancellationToken).AsTask()
		);

		Assert.Equal(1, await DeleteCursorAsync(fixture.Options, "returning", cancellationToken));
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
			await Enqueue(first, now, string.Create(CultureInfo.InvariantCulture, $"job-{index}"), index, string.Create(CultureInfo.InvariantCulture, $"group-{index % 3}"), cancellationToken);

		var claims = await Task.WhenAll(
			first.AcquireDueJobsAsync(CreateFairRequest("worker-a", 12), cancellationToken).AsTask(),
			second.AcquireDueJobsAsync(CreateFairRequest("worker-b", 12), cancellationToken).AsTask()
		).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
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
			await storage.CompleteAsync(job.Id, 1, "worker-1", cancellationToken);

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

		await storage.CompleteAsync(acquired.Id, 1, "worker", cancellationToken);

		var completed = Assert.Single(await storage.QueryJobsAsync(new() { Id = acquired.Id }, cancellationToken));
		Assert.Equal(JobState.Succeeded, completed.State);
	}

	[Fact]
	public async Task BufferedContinuationsRejectInvalidBatchRelationshipsBeforeMutation()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 0) with { Id = "current", BatchId = "batch" };
		await storage.EnqueueBatchAsync(new()
		{
			Id = "batch",
			CreatedAt = now,
			TotalJobs = 1,
			PendingCount = 1,
			State = BatchState.Executing,
		}, [current], [], cancellationToken);
		_ = Assert.Single(await storage.AcquireJobsAsync(
			[current.Id], "worker", TimeSpan.FromMinutes(1), cancellationToken
		));

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			1, "worker",
			[new()
			{
				Job = CreateJob(now, 1) with { Id = "detached", BatchId = "batch" },
				Options = ContinuationOptions.Detached,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			1, "worker",
			[new()
			{
				Job = CreateJob(now, 2) with { Id = "wrong-batch", BatchId = "other" },
				Options = ContinuationOptions.BesideContinuations,
			}],
			cancellationToken
		).AsTask());

		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.Id, cancellationToken))!.State);
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "detached" }, cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "wrong-batch" }, cancellationToken));
	}

	[Fact]
	public async Task BufferedContinuationsRejectUnknownOptionsAndTriggersBeforeMutation()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 0) with { Id = "current" };
		await storage.EnqueueAsync(current, cancellationToken);
		_ = Assert.Single(await storage.AcquireJobsAsync(
			[current.Id], "worker", TimeSpan.FromMinutes(1), cancellationToken
		));

		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			1, "worker",
			[new()
			{
				Job = CreateJob(now, 1) with { Id = "unknown-option" },
				Options = (ContinuationOptions)42,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			1, "worker",
			[new()
			{
				Job = CreateJob(now, 2) with { Id = "unknown-trigger" },
				Options = ContinuationOptions.Detached,
				Trigger = (ContinuationTrigger)42,
			}],
			cancellationToken
		).AsTask());

		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.Id, cancellationToken))!.State);
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "unknown-option" }, cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "unknown-trigger" }, cancellationToken));
	}

	[Fact]
	public async Task AddBatchJobRejectsWrongBatchAndUnknownOptionBeforeMutation()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 0) with { Id = "current", BatchId = "batch" };
		await storage.EnqueueBatchAsync(new()
		{
			Id = "batch",
			CreatedAt = now,
			TotalJobs = 1,
			PendingCount = 1,
			State = BatchState.Executing,
		}, [current], [], cancellationToken);
		_ = Assert.Single(await storage.AcquireJobsAsync(
			[current.Id], "worker", TimeSpan.FromMinutes(1), cancellationToken
		));

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.AddBatchJobAsync(
			current.Id,
			1,
			CreateJob(now, 1) with { Id = "wrong-batch", BatchId = "other" },
			ContinuationOptions.BesideContinuations,
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.AddBatchJobAsync(
			current.Id,
			1,
			CreateJob(now, 2) with { Id = "unknown-option", BatchId = "batch" },
			(ContinuationOptions)42,
			cancellationToken
		).AsTask());

		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "wrong-batch" }, cancellationToken));
		Assert.Empty(await storage.QueryJobsAsync(new() { Id = "unknown-option" }, cancellationToken));
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
		await storage.CompleteAsync(parent.Id, 1, "worker", cancellationToken);
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
			FairQueues = policy ?? new FairQueuePolicy { ConcurrencyShareThreshold = 0.10, MinInflightForNoisy = 30, GroupRoundRobin = true },
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

	private static async Task InsertCursorAsync(
		DataOptions options,
		string groupId,
		long sequence,
		CancellationToken cancellationToken
	)
	{
		await using var connection = new DataConnection(options);
		_ = await connection.ExecuteAsync(
			string.Create(CultureInfo.InvariantCulture, $"""
			INSERT INTO "immediate_fair_queue_groups" ("QueueName", "GroupId", "LastServedSequence", "ConcurrencyStamp")
			VALUES ('{JobQueueDefinition.DefaultName}', '{groupId}', {sequence}, '{Guid.NewGuid()}')
			"""),
			cancellationToken
		);
	}

	private static async Task<int> DeleteCursorAsync(
		DataOptions options,
		string groupId,
		CancellationToken cancellationToken
	)
	{
		await using var connection = new DataConnection(options);
		return await connection.ExecuteAsync(
			$"""
			DELETE FROM "immediate_fair_queue_groups"
			WHERE "QueueName" = '{JobQueueDefinition.DefaultName}' AND "GroupId" = '{groupId}'
			""",
			cancellationToken
		);
	}

	private static JobRecord CreateJob(DateTimeOffset now, int index) => new()
	{
		Id = "job-" + Guid.NewGuid().ToString("N"),
		JobName = "storage-test",
		Payload = string.Create(CultureInfo.InvariantCulture, $"{{\"index\":{index}}}"),
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

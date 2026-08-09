using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

public sealed class EntityFrameworkCoreJobStorageTests
{
	[Fact]
	public async Task CompetingNodesClaimEachInvocationOnce()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();

		foreach (var index in Enumerable.Range(0, 64))
			await first.EnqueueAsync(CreateJob(now, index), cancellationToken);

		var firstClaim = first.AcquireDueJobsAsync(CreateRequest("node-a", 64), cancellationToken).AsTask();
		var secondClaim = second.AcquireDueJobsAsync(CreateRequest("node-b", 64), cancellationToken).AsTask();
		var claims = await Task.WhenAll(firstClaim, secondClaim);
		var claimed = claims.SelectMany(static claim => claim).ToArray();
		Assert.Equal(64, claimed.Length);
		Assert.Equal(64, claimed.Select(job => job.Id).Distinct().Count());
	}

	[Fact]
	public async Task CompetingFairQueueNodesClaimDistinctInvocations()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await first.EnqueueAsync(CreateJob(now, 1) with { Id = "group-a-job", GroupId = "group-a" }, cancellationToken);
		await first.EnqueueAsync(CreateJob(now, 2) with { Id = "group-b-job", GroupId = "group-b" }, cancellationToken);

		var firstClaim = first.AcquireDueJobsAsync(CreateFairRequest("node-a", 1), cancellationToken).AsTask();
		var secondClaim = second.AcquireDueJobsAsync(CreateFairRequest("node-b", 1), cancellationToken).AsTask();
		var claimed = (await Task.WhenAll(firstClaim, secondClaim)).SelectMany(static jobs => jobs).ToArray();

		Assert.Equal(2, claimed.Length);
		Assert.Equal(2, claimed.Select(static job => job.Id).Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task ExpiredLeaseIsRecoveredByAnotherNode()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1);
		await first.EnqueueAsync(job, cancellationToken);

		_ = Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));

		var recovered = Assert.Single(
			await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken)
		);
		Assert.Equal(job.Id, recovered.Id);
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-b", recovered.WorkerId);
	}

	[Fact]
	public async Task CancellationRetriesAfterConcurrencyConflict()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var interceptor = new CancelConcurrencyInterceptor();
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			interceptor: interceptor
		);
		var storage = fixture.CreateStorage();
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with { Id = "cancel-contention" };
		await storage.EnqueueAsync(job, cancellationToken);

		await storage.CancelAsync(job.Id, cancellationToken);

		Assert.Equal(1, interceptor.Conflicts);
		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(job.Id, cancellationToken))!.State);
	}

	[Fact]
	public async Task GroupedEnqueueResetsCursorTransactionallyOnlyForReturningGroups()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var original = CreateJob(now, 1) with { Id = "returning-original", GroupId = "returning-group" };
		await storage.EnqueueAsync(original, cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateFairRequest("cursor-worker", 1), cancellationToken));
		await storage.CompleteAsync(original.Id, 1, "cursor-worker", cancellationToken);
		await fixture.InsertCursorAsync("returning-group", 42, cancellationToken);

		_ = await Assert.ThrowsAsync<DbUpdateException>(() =>
			storage.EnqueueAsync(original, cancellationToken).AsTask()
		);

		Assert.Equal(42, await fixture.GetCursorAsync("returning-group", cancellationToken));
		await storage.EnqueueAsync(
			CreateJob(now, 2) with { Id = "returning-new", GroupId = "returning-group" },
			cancellationToken
		);
		Assert.Null(await fixture.GetCursorAsync("returning-group", cancellationToken));

		await fixture.InsertCursorAsync("live-group", 73, cancellationToken);
		await storage.EnqueueAsync(
			CreateJob(now, 3) with { Id = "live-first", GroupId = "live-group" },
			cancellationToken
		);
		await fixture.InsertCursorAsync("live-group", 73, cancellationToken, replace: true);
		await storage.EnqueueAsync(
			CreateJob(now, 4) with { Id = "live-second", GroupId = "live-group" },
			cancellationToken
		);
		Assert.Equal(73, await fixture.GetCursorAsync("live-group", cancellationToken));
	}

	[Fact]
	public async Task VanishedFairQueueCandidateStateDoesNotThrow()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var interceptor = new FairQueueRaceInterceptor("vanished", deleteCandidateDuringStateRead: true);
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken, interceptor: interceptor);
		var storage = fixture.CreateStorage();
		await storage.EnqueueAsync(
			CreateJob(fixture.TimeProvider.GetUtcNow(), 1) with { Id = "vanished", GroupId = "race-group" },
			cancellationToken
		);

		var acquired = await storage.AcquireDueJobsAsync(CreateFairRequest("race-worker", 1), cancellationToken);

		Assert.Empty(acquired);
		Assert.True(interceptor.CandidateDeleted);
	}

	[Fact]
	public async Task FairQueueStopsAfterBoundedConsecutiveClaimLosses()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var interceptor = new FairQueueRaceInterceptor("pending", sabotageCursorClaims: true);
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken, interceptor: interceptor);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { Id = "active", GroupId = "loss-group" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { Id = "pending", GroupId = "loss-group" }, cancellationToken);
		interceptor.Enabled = false;
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateFairRequest("seed-worker", 1), cancellationToken));
		interceptor.Enabled = true;

		var acquisition = storage.AcquireDueJobsAsync(CreateFairRequest("loss-worker", 1), cancellationToken).AsTask();
		var acquired = await acquisition.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

		Assert.Empty(acquired);
		Assert.InRange(interceptor.SabotagedClaims, 1, 10);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync("pending", cancellationToken))!.State);
	}

	[Fact]
	public async Task SingleServerRestoresDurableEfJobsIntoMemory()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		await using var firstProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		var job = CreateJob(fixture.TimeProvider.GetUtcNow(), 1);
		await firstProcess.EnqueueAsync(job, cancellationToken);

		await using var restartedProcess = new SingleServerJobStorage(fixture.CreateStorage(), fixture.TimeProvider);
		await restartedProcess.InitializeAsync(cancellationToken);

		Assert.Equal(job.Id, Assert.Single(await restartedProcess.QueryJobsAsync(new(), cancellationToken)).Id);
		Assert.Equal(
			job.Id,
			Assert.Single(await restartedProcess.AcquireDueJobsAsync(CreateRequest("restarted", 1), cancellationToken)).Id
		);
	}

	[Fact]
	public async Task SingleServerMirrorsFairSelectionAndGroupIds()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var now = fixture.TimeProvider.GetUtcNow();
		await using (var firstProcess = new SingleServerJobStorage(
			fixture.CreateStorage(),
			fixture.TimeProvider
		))
		{
			await firstProcess.EnqueueAsync(
				CreateJob(now, 1) with { Id = "a-first", GroupId = "group-a" },
				cancellationToken
			);
			await firstProcess.EnqueueAsync(
				CreateJob(now, 2) with { Id = "a-second", GroupId = "group-a" },
				cancellationToken
			);
			var request = CreateFairRequest("single-server", 1);
			var first = Assert.Single(await firstProcess.AcquireDueJobsAsync(request, cancellationToken));
			Assert.Equal("a-first", first.Id);
			await firstProcess.CompleteAsync(first.Id, 1, "single-server", cancellationToken);
			await firstProcess.EnqueueAsync(
				CreateJob(now, 3) with { Id = "b-first", GroupId = "group-b" },
				cancellationToken
			);

			var second = Assert.Single(await firstProcess.AcquireDueJobsAsync(request, cancellationToken));
			Assert.Equal("b-first", second.Id);
			Assert.Equal("group-b", second.GroupId);
			var durableSecond = Assert.Single(await firstProcess.DurableStorage.QueryJobsAsync(
				new() { Id = second.Id },
				cancellationToken
			));
			Assert.Equal(JobState.Active, durableSecond.State);
			Assert.Equal("group-b", durableSecond.GroupId);
		}

		await using var restartedProcess = new SingleServerJobStorage(
			fixture.CreateStorage(),
			fixture.TimeProvider
		);
		await restartedProcess.InitializeAsync(cancellationToken);
		var restored = await restartedProcess.QueryJobsAsync(new(), cancellationToken);
		Assert.Contains(restored, static job => string.Equals(job.Id, "a-second", StringComparison.Ordinal) && string.Equals(job.GroupId, "group-a", StringComparison.Ordinal));
		Assert.Contains(restored, static job => string.Equals(job.Id, "b-first", StringComparison.Ordinal) && string.Equals(job.GroupId, "group-b", StringComparison.Ordinal));
	}

	[Fact]
	public async Task FairQueueAcquisitionRunsInsideConfiguredExecutionStrategy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			useRetryingExecutionStrategy: true
		);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(CreateJob(now, 1) with { Id = "a-first", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 2) with { Id = "a-second", GroupId = "group-a" }, cancellationToken);
		await storage.EnqueueAsync(CreateJob(now, 3) with { Id = "b-first", GroupId = "group-b" }, cancellationToken);
		var request = CreateFairRequest("fair-worker", 1);

		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("a-first", first.Id);
		await storage.CompleteAsync(first.Id, 1, "fair-worker", cancellationToken);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("b-first", second.Id);
	}

	[Fact]
	public async Task RecurringMaterializationRunsInsideConfiguredExecutionStrategy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			useRetryingExecutionStrategy: true
		);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var nextRunAt = now.AddMinutes(1);
		var schedule = new RecurringJobSchedule
		{
			Name = "retrying-strategy",
			JobName = "ef-test",
			Cron = "0 * * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		var job = CreateJob(now, 1) with
		{
			RecurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{schedule.NextRunAt.UtcTicks}"),
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);

		var materialized = await storage.MaterializeRecurringAsync(
			schedule,
			job,
			nextRunAt,
			cancellationToken
		);

		Assert.True(materialized);
		Assert.Equal(job.Id, Assert.Single(await storage.QueryJobsAsync(new(), cancellationToken)).Id);
		var storedSchedule = Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
		Assert.Equal(now, storedSchedule.LastRunAt);
		Assert.Equal(nextRunAt, storedSchedule.NextRunAt);
	}

	[Theory]
	[InlineData(ContinuationOptions.Detached, "unexpected-batch")]
	[InlineData(ContinuationOptions.BesideContinuations, "wrong-batch")]
	public async Task CompletionRejectsInvalidContinuationBatchIdsBeforeMutation(
		ContinuationOptions options,
		string invalidBatchId
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 1) with { Id = "validation-current", BatchId = "validation-batch" };
		await storage.EnqueueBatchAsync(
			new()
			{
				Id = "validation-batch",
				CreatedAt = now,
				TotalJobs = 1,
				PendingCount = 1,
				State = BatchState.Executing,
			},
			[current],
			[],
			cancellationToken
		);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("validation-worker", 1), cancellationToken));

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			1, "validation-worker",
			[new()
			{
				Job = CreateJob(now, 2) with { Id = "validation-child", BatchId = invalidBatchId },
				Options = options,
			}],
			cancellationToken
		).AsTask());

		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.Id, cancellationToken))!.State);
		Assert.Null(await storage.GetJobStatusAsync("validation-child", cancellationToken));
		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("validation-batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.AddBatchJobAsync(
			current.Id,
			1,
			CreateJob(now, 3) with { Id = "validation-added", BatchId = "wrong-batch" },
			ContinuationOptions.BesideContinuations,
			cancellationToken
		).AsTask());
		Assert.Null(await storage.GetJobStatusAsync("validation-added", cancellationToken));
		batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("validation-batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
	}

	[Fact]
	public async Task CompletionRejectsUnknownContinuationOptionsAndTriggersBeforeMutation()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob(now, 1) with { Id = "enum-current" };
		await storage.EnqueueAsync(current, cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("enum-worker", 1), cancellationToken));

		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			1, "enum-worker",
			[new()
			{
				Job = CreateJob(now, 2) with { Id = "unknown-trigger" },
				Options = ContinuationOptions.Detached,
				Trigger = (ContinuationTrigger)int.MaxValue,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			1, "enum-worker",
			[new()
			{
				Job = CreateJob(now, 3) with { Id = "unknown-option" },
				Options = (ContinuationOptions)int.MaxValue,
			}],
			cancellationToken
		).AsTask());

		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.Id, cancellationToken))!.State);
		Assert.Null(await storage.GetJobStatusAsync("unknown-trigger", cancellationToken));
		Assert.Null(await storage.GetJobStatusAsync("unknown-option", cancellationToken));
	}

	[Fact]
	public async Task EmptyBatchProjectionIsFullySettled()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(cancellationToken);
		var storage = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await fixture.InsertEmptyBatchAsync("empty-batch", now, cancellationToken);

		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("empty-batch", cancellationToken));
		Assert.Equal(1d, batch.FractionSettled);
	}

	[Fact]
	public async Task PurgeRunsInsideConfiguredExecutionStrategy()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await StorageFixture.CreateAsync(
			cancellationToken,
			useRetryingExecutionStrategy: true
		);
		var storage = fixture.CreateStorage();

		await storage.PurgeJobsAsync(
			TimeSpan.FromHours(1),
			TimeSpan.FromHours(1),
			cancellationToken
		);
		await storage.PurgeBatchesAsync(
			TimeSpan.FromHours(1),
			TimeSpan.FromHours(1),
			cancellationToken
		);
	}

	private static JobAcquisitionRequest CreateRequest(string workerId, int batchSize) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = batchSize,
		Queues = [new() { QueueName = JobQueueDefinition.DefaultName, Capacity = batchSize, JobCapacities = new Dictionary<string, int> { ["ef-test"] = batchSize } }],
	};

	private static JobAcquisitionRequest CreateFairRequest(
		string workerId,
		int batchSize,
		FairQueuePolicy? policy = null
	) => CreateRequest(workerId, batchSize) with
	{
		FairQueues = policy ?? new FairQueuePolicy
		{
			ConcurrencyShareThreshold = 0.10,
			MinInflightForNoisy = 30,
			GroupRoundRobin = true,
		},
	};

	private static JobRecord CreateJob(DateTimeOffset now, int index) => new()
	{
		Id = "job-" + Guid.NewGuid().ToString("N"),
		JobName = "ef-test",
		Payload = string.Create(CultureInfo.InvariantCulture, $"{{\"index\":{index}}}"),
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now.AddTicks(index),
	};

	private sealed class StorageFixture(
		string connectionString,
		ServiceProvider services,
		IDbContextFactory<TestDbContext> contextFactory,
		FakeTimeProvider timeProvider
	) : IAsyncDisposable
	{
		private readonly SqliteConnection _anchor = new(connectionString);

		public FakeTimeProvider TimeProvider { get; } = timeProvider;

		public EntityFrameworkCoreJobStorage<TestDbContext> CreateStorage() => new(contextFactory, TimeProvider);

		public async Task InsertCursorAsync(
			string groupId,
			long sequence,
			CancellationToken cancellationToken,
			bool replace = false
		)
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			var format = replace
				? "INSERT OR REPLACE INTO immediate_fair_queue_groups (QueueName, GroupId, LastServedSequence, ConcurrencyStamp) VALUES ({0}, {1}, {2}, {3})"
				: "INSERT INTO immediate_fair_queue_groups (QueueName, GroupId, LastServedSequence, ConcurrencyStamp) VALUES ({0}, {1}, {2}, {3})";
			var command = FormattableStringFactory.Create(
				format,
				JobQueueDefinition.DefaultName,
				groupId,
				sequence,
				Guid.NewGuid()
			);
			_ = await context.Database.ExecuteSqlAsync(command, cancellationToken);
		}

		public async Task<long?> GetCursorAsync(string groupId, CancellationToken cancellationToken)
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			await context.Database.OpenConnectionAsync(cancellationToken);
			await using var command = context.Database.GetDbConnection().CreateCommand();
			command.CommandText = "SELECT LastServedSequence FROM immediate_fair_queue_groups WHERE QueueName = $queue AND GroupId = $group";
			var queueParameter = command.CreateParameter();
			queueParameter.ParameterName = "$queue";
			queueParameter.Value = JobQueueDefinition.DefaultName;
			_ = command.Parameters.Add(queueParameter);
			var groupParameter = command.CreateParameter();
			groupParameter.ParameterName = "$group";
			groupParameter.Value = groupId;
			_ = command.Parameters.Add(groupParameter);
			var value = await command.ExecuteScalarAsync(cancellationToken);
			return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
		}

		public async Task InsertEmptyBatchAsync(
			string batchId,
			DateTimeOffset createdAt,
			CancellationToken cancellationToken
		)
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			_ = await context.Database.ExecuteSqlAsync(
				$"""INSERT INTO immediate_job_batches (Id, CreatedAt, TotalJobs, PendingCount, SucceededCount, FailedCount, CancelledCount, SkippedCount, StartedAt, CompletedAt, State, ConcurrencyStamp) VALUES ({batchId}, {createdAt.UtcTicks}, 0, 0, 0, 0, 0, 0, NULL, {createdAt.UtcTicks}, {(short)BatchState.Succeeded}, {Guid.NewGuid()})""",
				cancellationToken
			);
		}

		public static async Task<StorageFixture> CreateAsync(
			CancellationToken cancellationToken,
			bool useRetryingExecutionStrategy = false,
			IInterceptor? interceptor = null
		)
		{
			var connectionString = $"Data Source=jobs-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
			if (interceptor is FairQueueRaceInterceptor raceInterceptor)
				raceInterceptor.ConnectionString = connectionString;
			var services = new ServiceCollection();
			_ = services.AddDbContextFactory<TestDbContext>(options =>
			{
				_ = options.UseSqlite(connectionString);
				if (interceptor is not null)
					_ = options.AddInterceptors(interceptor);
				if (useRetryingExecutionStrategy)
					_ = options.ReplaceService<IExecutionStrategyFactory, RetryingExecutionStrategyFactory>();
			});
			var provider = services.BuildServiceProvider();
			var factory = provider.GetRequiredService<IDbContextFactory<TestDbContext>>();
			var fixture = new StorageFixture(
				connectionString,
				provider,
				factory,
				new(new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero))
			);
			try
			{
				await fixture._anchor.OpenAsync(cancellationToken);
				await using var context = await factory.CreateDbContextAsync(cancellationToken);
				_ = await context.Database.EnsureCreatedAsync(cancellationToken);
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
			await services.DisposeAsync();
			await _anchor.DisposeAsync();
		}
	}

	private sealed class CancelConcurrencyInterceptor : SaveChangesInterceptor
	{
		private int _conflicts;

		public int Conflicts => Volatile.Read(ref _conflicts);

		public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
			DbContextEventData eventData,
			InterceptionResult<int> result,
			CancellationToken cancellationToken = default
		)
		{
			var cancelling = eventData.Context?.ChangeTracker
				.Entries()
				.Any(static entry =>
					entry.State == EntityState.Modified
					&& entry.Properties.Any(static property =>
						property.IsModified
						&& string.Equals(property.Metadata.Name, "State", StringComparison.Ordinal)
						&& Equals(property.CurrentValue, JobState.Cancelled))) == true;
			if (cancelling && Interlocked.CompareExchange(ref _conflicts, 1, 0) == 0)
				throw new DbUpdateConcurrencyException("Injected cancellation concurrency conflict.");

			return base.SavingChangesAsync(eventData, result, cancellationToken);
		}
	}

#pragma warning disable IDE0290 // An explicit constructor keeps captured test configuration unambiguous.
	private sealed class FairQueueRaceInterceptor : DbCommandInterceptor
	{
		private readonly string _candidateId;
		private readonly bool _deleteCandidateDuringStateRead;
		private readonly bool _sabotageCursorClaims;
		private int _candidateDeleted;
		private int _sabotagedClaims;

		public FairQueueRaceInterceptor(
			string candidateId,
			bool deleteCandidateDuringStateRead = false,
			bool sabotageCursorClaims = false
		)
		{
			_candidateId = candidateId;
			_deleteCandidateDuringStateRead = deleteCandidateDuringStateRead;
			_sabotageCursorClaims = sabotageCursorClaims;
			Enabled = deleteCandidateDuringStateRead;
		}

		public string ConnectionString { get; set; } = null!;
		public bool Enabled { get; set; }
		public bool CandidateDeleted => Volatile.Read(ref _candidateDeleted) != 0;
		public int SabotagedClaims => Volatile.Read(ref _sabotagedClaims);

		public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
			DbCommand command,
			CommandEventData eventData,
			InterceptionResult<DbDataReader> result,
			CancellationToken cancellationToken = default
		)
		{
			if (!Enabled)
				return result;

			var sql = command.CommandText;
			if (_deleteCandidateDuringStateRead
				&& Volatile.Read(ref _candidateDeleted) == 0
				&& sql.Contains("immediate_jobs", StringComparison.Ordinal)
				&& sql.Contains("immediate_fair_queue_groups", StringComparison.Ordinal))
			{
				await ExecuteMutationAsync(
					"DELETE FROM immediate_jobs WHERE Id = $id",
					("$id", _candidateId),
					cancellationToken
				);
				_ = Interlocked.Exchange(ref _candidateDeleted, 1);
			}
			else if (_sabotageCursorClaims
				&& sql.Contains("immediate_fair_queue_groups", StringComparison.Ordinal)
				&& !sql.Contains("immediate_jobs", StringComparison.Ordinal)
				&& !sql.Contains("MAX(", StringComparison.OrdinalIgnoreCase))
			{
#pragma warning disable CA2100 // Rewrites EF-generated SQL with a fixed arithmetic expression.
				command.CommandText = command.CommandText.Replace(
					"\"LastServedSequence\"",
					"\"LastServedSequence\" + 100",
					StringComparison.Ordinal
				);
#pragma warning restore CA2100
				_ = Interlocked.Increment(ref _sabotagedClaims);
			}

			return result;
		}

		private async Task ExecuteMutationAsync(
			string commandText,
			(string Name, object Value) parameter,
			CancellationToken cancellationToken
		) => await ExecuteMutationAsync(commandText, [parameter], cancellationToken);

		private async Task ExecuteMutationAsync(
			string commandText,
			IReadOnlyList<(string Name, object Value)> parameters,
			CancellationToken cancellationToken
		)
		{
			await using var connection = new SqliteConnection(ConnectionString);
			await connection.OpenAsync(cancellationToken);
			await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-only helper receives one of the fixed statements above.
			command.CommandText = commandText;
#pragma warning restore CA2100
			foreach (var (name, value) in parameters)
				_ = command.Parameters.AddWithValue(name, value);
			_ = await command.ExecuteNonQueryAsync(cancellationToken);
		}
	}
#pragma warning restore IDE0290

	private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
	{
		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			_ = modelBuilder.AddImmediateJobs();
		}
	}

	private sealed class RetryingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
		: IExecutionStrategyFactory
	{
		public IExecutionStrategy Create() => new RetryingExecutionStrategy(dependencies);
	}

	private sealed class RetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
		: ExecutionStrategy(dependencies, DefaultMaxRetryCount, DefaultMaxDelay)
	{
		protected override bool ShouldRetryOn(Exception exception) => false;
	}
}

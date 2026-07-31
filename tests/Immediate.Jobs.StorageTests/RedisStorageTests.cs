using System.Globalization;
using Immediate.Jobs.Redis;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Immediate.Jobs.StorageTests;

#pragma warning disable CS1591
[CollectionDefinition(Name)]
public sealed class RedisContainerFixtureGroup : ICollectionFixture<RedisStorageFixture>
{
	public const string Name = "Redis storage";
}

public sealed class RedisStorageFixture : IAsyncLifetime
{
	public RedisContainer Container { get; } = new RedisBuilder("redis:8-alpine").Build();

	public ValueTask InitializeAsync() => new(Container.StartAsync());

	public ValueTask DisposeAsync() => Container.DisposeAsync();
}

[Collection(RedisContainerFixtureGroup.Name)]
public sealed class RedisStorageTests(RedisStorageFixture fixture)
{
	[Fact]
	public async Task ClaimsEachJobOnceUnderContention()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var first = new RedisJobStorage(connection, options, timeProvider);
		await using var second = new RedisJobStorage(connection, options, timeProvider);
		foreach (var index in Enumerable.Range(0, 24))
			await first.EnqueueAsync(CreateJob(string.Create(CultureInfo.InvariantCulture, $"contended-{index}"), timeProvider.GetUtcNow()), cancellationToken);

		var claims = await Task.WhenAll(
			first.AcquireDueJobsAsync(CreateRequest("node-a", 24), cancellationToken).AsTask(),
			second.AcquireDueJobsAsync(CreateRequest("node-b", 24), cancellationToken).AsTask()
		);

		var claimed = claims.SelectMany(static claim => claim).ToArray();
		Assert.Equal(24, claimed.Length);
		Assert.Equal(24, claimed.Select(static job => job.Id).Distinct(StringComparer.Ordinal).Count());
		Assert.All(claimed, static job => Assert.Equal(JobState.Active, job.State));
	}

	[Fact]
	public async Task ExpiredLeaseIsRecoveredAndRejectsTheStaleOwner()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var first = new RedisJobStorage(connection, options, timeProvider);
		await using var second = new RedisJobStorage(connection, options, timeProvider);
		await first.EnqueueAsync(
			CreateJob("leased", timeProvider.GetUtcNow()) with { GroupId = "tenant-a" },
			cancellationToken
		);

		var firstClaim = Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		Assert.Equal("tenant-a", firstClaim.GroupId);
		await first.SetExecutionTelemetryAsync(
			"leased",
			"node-a",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			timeProvider.GetUtcNow(),
			cancellationToken
		);
		timeProvider.Advance(TimeSpan.FromSeconds(59));
		Assert.Empty(await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));

		timeProvider.Advance(TimeSpan.FromSeconds(2));
		var recovered = Assert.Single(await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-b", recovered.WorkerId);
		Assert.Equal("tenant-a", recovered.GroupId);
		Assert.Null(recovered.ExecutionTraceId);
		Assert.Null(recovered.ExecutionSpanId);
		Assert.Null(recovered.ExecutionStartedAt);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => first.CompleteAsync("leased", "node-a", cancellationToken).AsTask()
		);
	}

	[Fact]
	public async Task ExpiredLeaseRemainsRecoverableWhenItsQueueIsTemporarilyOmitted()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var storage = new RedisJobStorage(connection, options, timeProvider);
		await storage.EnqueueAsync(
			CreateJob("secondary", timeProvider.GetUtcNow()) with { QueueName = "secondary" },
			cancellationToken
		);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("node-a", 1, "secondary"),
			cancellationToken
		));
		timeProvider.Advance(TimeSpan.FromMinutes(2));

		Assert.Empty(await storage.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));
		var recovered = Assert.Single(await storage.AcquireDueJobsAsync(
			CreateRequest("node-b", 1, "secondary"),
			cancellationToken
		));
		Assert.Equal("secondary", recovered.Id);
		Assert.Equal(2, recovered.Attempt);
	}

	[Fact]
	public async Task UnfilteredQueryReadsOnlyTheRequestedRankWindow()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var storage = new RedisJobStorage(connection, options, timeProvider);
		await Task.WhenAll(Enumerable.Range(0, 3).Select(index => storage.EnqueueAsync(
			CreateJob(string.Create(CultureInfo.InvariantCulture, $"window-{index}"), timeProvider.GetUtcNow().AddMilliseconds(index)),
			cancellationToken
		).AsTask()));
		_ = await connection.GetDatabase(options.Database).HashSetAsync(
			JobKey(options, "window-0"),
			"record",
			"invalid json"
		);

		var jobs = await storage.QueryJobsAsync(new() { Skip = 1, Take = 1 }, cancellationToken);

		Assert.Equal("window-1", Assert.Single(jobs).Id);
	}

	[Fact]
	public async Task ExactIdQueryDoesNotReadUnrelatedJobs()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var storage = new RedisJobStorage(connection, options, timeProvider);
		await storage.EnqueueAsync(CreateJob("target", timeProvider.GetUtcNow()), cancellationToken);
		await storage.EnqueueAsync(CreateJob("poison", timeProvider.GetUtcNow().AddMilliseconds(1)), cancellationToken);
		_ = await connection.GetDatabase(options.Database).HashSetAsync(
			JobKey(options, "poison"),
			"record",
			"invalid json"
		);

		var jobs = await storage.QueryJobsAsync(new() { Id = "target", Take = 1 }, cancellationToken);

		Assert.Equal("target", Assert.Single(jobs).Id);
	}

	[Fact]
	public async Task FilteredPaginationScansMultipleWindowsAndStopsAfterTheRequestedMatches()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var storage = new RedisJobStorage(connection, options, timeProvider);
		await Task.WhenAll(Enumerable.Range(0, 520).Select(index =>
		{
			var job = CreateJob(string.Create(CultureInfo.InvariantCulture, $"filtered-{index:D3}"), timeProvider.GetUtcNow().AddMilliseconds(index));
			if (index is 250 or 510)
			{
				job = job with
				{
					State = JobState.Scheduled,
					QueueName = "priority",
					JobName = "Contains-Needle",
				};
			}

			return storage.EnqueueAsync(job, cancellationToken).AsTask();
		}));
		_ = await connection.GetDatabase(options.Database).HashSetAsync(
			JobKey(options, "filtered-000"),
			"record",
			"invalid json"
		);

		var jobs = await storage.QueryJobsAsync(new()
		{
			State = JobState.Scheduled,
			QueueName = "priority",
			Search = "needle",
			Skip = 1,
			Take = 1,
		}, cancellationToken);

		Assert.Equal("filtered-250", Assert.Single(jobs).Id);
	}

	[Fact]
	public async Task RetryFastForwardsScheduledJobs()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		await using var storage = new RedisJobStorage(connection, CreateOptions(), timeProvider);
		var job = CreateJob("scheduled-retry", timeProvider.GetUtcNow()) with
		{
			State = JobState.Scheduled,
			DueAt = timeProvider.GetUtcNow().AddHours(1),
			Attempt = 1,
			LastError = "expected failure",
		};
		await storage.EnqueueAsync(job, cancellationToken);

		await storage.RetryAsync(job.Id, cancellationToken);

		var retried = Assert.Single(await storage.QueryJobsAsync(new() { Id = job.Id }, cancellationToken));
		Assert.Equal(JobState.Pending, retried.State);
		Assert.Equal(timeProvider.GetUtcNow(), retried.DueAt);
		Assert.Equal(1, retried.Attempt);
		Assert.Equal(job.LastError, retried.LastError);

		var firstRun = job with { Id = "scheduled-first-run", Attempt = 0, LastError = null };
		await storage.EnqueueAsync(firstRun, cancellationToken);
		await storage.RetryAsync(firstRun.Id, cancellationToken);
		var fastForwarded = Assert.Single(await storage.QueryJobsAsync(new() { Id = firstRun.Id }, cancellationToken));
		Assert.Equal(JobState.Pending, fastForwarded.State);
		Assert.Equal(timeProvider.GetUtcNow(), fastForwarded.DueAt);
		Assert.Equal(0, fastForwarded.Attempt);
	}

	[Fact]
	public async Task MissingDashboardActionsThrowKeyNotFoundException()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		await using var storage = new RedisJobStorage(connection, CreateOptions(), CreateTimeProvider());

		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.RetryAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.DeleteAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.RemoveRecurringAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.PauseRecurringAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.ResumeRecurringAsync("missing", cancellationToken).AsTask()
		);
	}

	[Fact]
	public async Task LowLevelStatusReportsUnknownMaxAttempts()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		await using var storage = new RedisJobStorage(connection, CreateOptions(), timeProvider);
		await storage.EnqueueAsync(CreateJob("status", timeProvider.GetUtcNow()), cancellationToken);

		var status = await storage.GetJobStatusAsync("status", cancellationToken);

		Assert.NotNull(status);
		Assert.Null(status.MaxAttempts);
	}

	[Fact]
	public async Task ExistingResourcesWithInvalidActionsThrowImmediateJobException()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		await using var storage = new RedisJobStorage(connection, CreateOptions(), timeProvider);
		await storage.EnqueueAsync(CreateJob("pending", timeProvider.GetUtcNow()), cancellationToken);
		await storage.UpsertRecurringAsync(new()
		{
			Name = "code-defined",
			JobName = "test-job",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = timeProvider.GetUtcNow(),
		}, cancellationToken);

		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.RetryAsync("pending", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.DeleteAsync("pending", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.RemoveRecurringAsync("code-defined", cancellationToken).AsTask()
		);
	}

	[Fact]
	public async Task PurgeRemovesStaleCompletedMembersWithoutLooping()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var storage = new RedisJobStorage(connection, options, timeProvider);
		await storage.EnqueueAsync(CreateJob("wrong-state", timeProvider.GetUtcNow()), cancellationToken);
		var database = connection.GetDatabase(options.Database);
		var completedKey = CompletedKey(options, JobState.Succeeded);
		var staleScore = timeProvider.GetUtcNow().AddMinutes(-1).ToUnixTimeMilliseconds();
		_ = await database.SortedSetAddAsync(completedKey, "missing", staleScore);
		_ = await database.SortedSetAddAsync(completedKey, "wrong-state", staleScore);

		await storage.PurgeJobsAsync(TimeSpan.Zero, TimeSpan.Zero, cancellationToken)
			.AsTask()
			.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

		Assert.Equal(0, await database.SortedSetLengthAsync(completedKey));
		Assert.NotNull(await storage.GetJobStatusAsync("wrong-state", cancellationToken));
	}

	[Fact]
	public async Task HeartbeatExpiresServerStateAfterTheLivenessWindow()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var storage = new RedisJobStorage(connection, options, timeProvider);
		await storage.HeartbeatAsync(new("node-a", timeProvider.GetUtcNow(), 1, 8), cancellationToken);

		var expiry = await connection.GetDatabase(options.Database).KeyTimeToLiveAsync(ServerKey(options, "node-a"));

		Assert.InRange(Assert.NotNull(expiry), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
	}

	[Fact]
	public async Task SnapshotSkipsServersWhoseStateAlreadyExpired()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var storage = new RedisJobStorage(connection, options, timeProvider);
		var now = timeProvider.GetUtcNow();
		await storage.HeartbeatAsync(new("node-a", now, 1, 8), cancellationToken);
		await storage.HeartbeatAsync(new("node-b", now, 2, 8), cancellationToken);
		_ = await connection.GetDatabase(options.Database).KeyDeleteAsync(ServerKey(options, "node-a"));

		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);

		Assert.Equal("node-b", Assert.Single(snapshot.Servers).WorkerId);
	}

	[Fact]
	public async Task ConcurrentRecurringMaterializationCreatesOneOccurrence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		await using var first = new RedisJobStorage(connection, options, timeProvider);
		await using var second = new RedisJobStorage(connection, options, timeProvider);
		var now = timeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "hourly",
			JobName = "test-job",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		await first.UpsertRecurringAsync(schedule, cancellationToken);
		var recurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{schedule.NextRunAt.UtcTicks}");

		var results = await Task.WhenAll(
			first.MaterializeRecurringAsync(
				schedule,
				CreateJob("occurrence-a", now) with { RecurringKey = recurringKey },
				now.AddHours(1),
				cancellationToken
			).AsTask(),
			second.MaterializeRecurringAsync(
				schedule,
				CreateJob("occurrence-b", now) with { RecurringKey = recurringKey },
				now.AddHours(1),
				cancellationToken
			).AsTask()
		);

		_ = Assert.Single(results, static inserted => inserted);
		var jobs = await first.QueryJobsAsync(new() { Search = "test-job" }, cancellationToken);
		_ = Assert.Single(jobs);
		var persisted = Assert.Single((await first.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
		Assert.Equal(now, persisted.LastRunAt);
		Assert.Equal(now.AddHours(1), persisted.NextRunAt);
		Assert.Equal(StorageCapabilities.Queue | StorageCapabilities.Recurring, first.GetCapabilities());
		Assert.IsNotAssignableFrom<IJobGraphStorage>(first);
	}

	[Fact]
	public async Task StaleRecurringDueMemberDoesNotMaterializeFutureOccurrence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		using var storage = new RedisJobStorage(connection, options, timeProvider);
		var now = timeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "stale-due",
			JobName = "test-job",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);
		Assert.True(await storage.MaterializeRecurringAsync(
			schedule,
			CreateJob("first-occurrence", now) with
			{
				RecurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{now.UtcTicks}"),
			},
			now.AddHours(1),
			cancellationToken
		));
		var database = connection.GetDatabase(options.Database);
		_ = await database.SortedSetAddAsync(
			RecurringDueKey(options),
			RecurringDueMember(now, schedule.Name),
			now.ToUnixTimeMilliseconds()
		);
		var persisted = Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);

		Assert.False(await storage.MaterializeRecurringAsync(
			persisted,
			CreateJob("future-occurrence", persisted.NextRunAt) with
			{
				RecurringKey = string.Create(
					CultureInfo.InvariantCulture,
					$"{schedule.Name}:{persisted.NextRunAt.UtcTicks}"
				),
			},
			persisted.NextRunAt.AddHours(1),
			cancellationToken
		));
		Assert.Empty(await storage.GetDueRecurringAsync(now, 10, cancellationToken));

		var member = Assert.Single(await database.SortedSetRangeByRankAsync(RecurringDueKey(options)));
		Assert.Equal(RecurringDueMember(now.AddHours(1), schedule.Name), (string)member!);
		Assert.Null(await storage.GetJobStatusAsync("future-occurrence", cancellationToken));
	}

	[Fact]
	public async Task DuplicateRecurringDueMembersForSameScheduleAreDeduplicated()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		using var storage = new RedisJobStorage(connection, options, timeProvider);
		var now = timeProvider.GetUtcNow();
		var dueAt = now.AddHours(1);
		var schedule = new RecurringJobSchedule
		{
			Name = "duplicate-due",
			JobName = "test-job",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = dueAt,
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);
		var database = connection.GetDatabase(options.Database);
		_ = await database.SortedSetAddAsync(
			RecurringDueKey(options),
			RecurringDueMember(now, schedule.Name),
			now.ToUnixTimeMilliseconds()
		);

		var due = Assert.Single(await storage.GetDueRecurringAsync(dueAt, 10, cancellationToken));

		Assert.Equal(dueAt, due.NextRunAt);
		var member = Assert.Single(await database.SortedSetRangeByRankAsync(RecurringDueKey(options)));
		Assert.Equal(RecurringDueMember(dueAt, schedule.Name), (string)member!);
	}

	[Fact]
	public async Task PauseResumeRaceKeepsOnlyTheCurrentDueMember()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		using var storage = new RedisJobStorage(connection, options, timeProvider);
		var database = connection.GetDatabase(options.Database);
		var now = timeProvider.GetUtcNow();
		for (var index = 0; index < 20; index++)
		{
			var schedule = new RecurringJobSchedule
			{
				Name = string.Create(CultureInfo.InvariantCulture, $"pause-race-{index}"),
				JobName = "test-job",
				Cron = "0 * * * *",
				TimeZone = "UTC",
				IsCodeDefined = true,
				NextRunAt = now,
			};
			await storage.UpsertRecurringAsync(schedule, cancellationToken);
			var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var materialize = Task.Run(async () =>
			{
				await start.Task;
				_ = await storage.MaterializeRecurringAsync(
					schedule,
					CreateJob(string.Create(CultureInfo.InvariantCulture, $"pause-race-job-{index}"), now) with
					{
						RecurringKey = string.Create(
							CultureInfo.InvariantCulture,
							$"{schedule.Name}:{now.UtcTicks}"
						),
					},
					now.AddHours(1),
					cancellationToken
				);
			}, cancellationToken);
			var pause = Task.Run(async () =>
			{
				await start.Task;
				await storage.PauseRecurringAsync(schedule.Name, cancellationToken);
			}, cancellationToken);
			var resume = Task.Run(async () =>
			{
				await start.Task;
				await storage.ResumeRecurringAsync(schedule.Name, cancellationToken);
			}, cancellationToken);

			start.SetResult();
			await Task.WhenAll(materialize, pause, resume);

			var persisted = (await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring
				.Single(item => string.Equals(item.Name, schedule.Name, StringComparison.Ordinal));
			var members = (await database.SortedSetRangeByRankAsync(RecurringDueKey(options)))
				.Select(static member => (string)member!)
				.Where(member => member.EndsWith($"|{schedule.Name}", StringComparison.Ordinal))
				.ToArray();
			if (persisted.IsPaused)
				Assert.Empty(members);
			else
				Assert.Equal(RecurringDueMember(persisted.NextRunAt, schedule.Name), Assert.Single(members));
		}
	}

	[Fact]
	public async Task RecurringDedupeHitAdvancesScheduleWithoutDuplicateJob()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		using var storage = new RedisJobStorage(connection, CreateOptions(), timeProvider);
		var now = timeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "dedupe-advance",
			JobName = "test-job",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		var recurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{now.UtcTicks}");
		await storage.UpsertRecurringAsync(schedule, cancellationToken);
		Assert.True(await storage.MaterializeRecurringAsync(
			schedule,
			CreateJob("dedupe-original", now) with { RecurringKey = recurringKey },
			now.AddHours(1),
			cancellationToken
		));
		await storage.UpsertRecurringAsync(schedule, cancellationToken);

		Assert.False(await storage.MaterializeRecurringAsync(
			schedule,
			CreateJob("dedupe-duplicate", now) with { RecurringKey = recurringKey },
			now.AddHours(1),
			cancellationToken
		));

		var persisted = Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
		Assert.Equal(now, persisted.LastRunAt);
		Assert.Equal(now.AddHours(1), persisted.NextRunAt);
		Assert.Null(await storage.GetJobStatusAsync("dedupe-duplicate", cancellationToken));
		_ = Assert.Single(await storage.QueryJobsAsync(new() { Search = "test-job" }, cancellationToken));
	}

	[Fact]
	public async Task PurgingRecurringJobRemovesItsDedupeEntry()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		var options = CreateOptions();
		using var storage = new RedisJobStorage(connection, options, timeProvider);
		var now = timeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "dedupe-purge",
			JobName = "test-job",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);
		Assert.True(await storage.MaterializeRecurringAsync(
			schedule,
			CreateJob("purged-occurrence", now) with
			{
				RecurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{now.UtcTicks}"),
			},
			now.AddHours(1),
			cancellationToken
		));
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		await storage.CompleteAsync("purged-occurrence", "worker", cancellationToken);
		var database = connection.GetDatabase(options.Database);
		Assert.Equal(1, await database.HashLengthAsync(RecurringDedupeKey(options)));
		timeProvider.Advance(TimeSpan.FromMilliseconds(1));

		await storage.PurgeJobsAsync(TimeSpan.Zero, TimeSpan.Zero, cancellationToken);

		Assert.Equal(0, await database.HashLengthAsync(RecurringDedupeKey(options)));
		Assert.Null(await storage.GetJobStatusAsync("purged-occurrence", cancellationToken));
	}

	[Fact]
	public async Task RecurringMaterializationPersistsSkippedOccurrenceAsSkipped()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var timeProvider = CreateTimeProvider();
		await using var storage = new RedisJobStorage(connection, CreateOptions(), timeProvider);
		var now = timeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "skip-overlap",
			JobName = "test-job",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);

		Assert.True(await storage.MaterializeRecurringAsync(
			schedule,
			CreateJob("skipped", now) with
			{
				State = JobState.Skipped,
				CompletedAt = now,
				RecurringKey = string.Create(CultureInfo.InvariantCulture, $"{schedule.Name}:{now.UtcTicks}"),
			},
			now.AddHours(1),
			cancellationToken
		));
		var skipped = Assert.Single(await storage.QueryJobsAsync(
			new() { State = JobState.Skipped },
			cancellationToken
		));
		Assert.Equal(now, skipped.CompletedAt);
		Assert.Empty(await storage.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
	}

	[Fact]
	public async Task UseRedisSelectsDistributedMode()
	{
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		var options = new ImmediateJobsOptions();
		_ = options.UseRedis(connection, redis => redis.KeyPrefix = $"test:{Guid.NewGuid():N}");
		Assert.Equal(JobStorageMode.Distributed, options.StorageMode);
	}

	[Fact]
	public async Task DisposingStorageDoesNotCloseAnApplicationOwnedConnection()
	{
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		await using var storage = new RedisJobStorage(connection, CreateOptions(), CreateTimeProvider());

		await storage.DisposeAsync();

		Assert.True(connection.IsConnected);
		_ = await connection.GetDatabase().PingAsync();
	}

	[Fact]
	public async Task FairQueueAcquisitionIsExplicitlyUnsupported()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.Container.GetConnectionString());
		await using var storage = new RedisJobStorage(connection, CreateOptions(), CreateTimeProvider());
		var request = CreateRequest("worker", 1) with
		{
			FairQueues = new(0.10, 30, GroupRoundRobin: true),
		};

		var exception = await Assert.ThrowsAsync<NotSupportedException>(
			() => storage.AcquireDueJobsAsync(request, cancellationToken).AsTask()
		);
		Assert.Contains("Redis", exception.Message, StringComparison.Ordinal);
	}

	private static FakeTimeProvider CreateTimeProvider() =>
		new(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));

	private static RedisJobStorageOptions CreateOptions() =>
		new() { KeyPrefix = $"immediate-jobs-test-{Guid.NewGuid():N}" };

	private static RedisKey JobKey(RedisJobStorageOptions options, string id) =>
		$"{{{options.KeyPrefix}}}:job:{id}";

	private static RedisKey CompletedKey(RedisJobStorageOptions options, JobState state) =>
		string.Create(CultureInfo.InvariantCulture, $"{{{options.KeyPrefix}}}:completed:{(int)state}");

	private static RedisKey ServerKey(RedisJobStorageOptions options, string workerId) =>
		$"{{{options.KeyPrefix}}}:server:{workerId}";

	private static RedisKey RecurringDueKey(RedisJobStorageOptions options) =>
		$"{{{options.KeyPrefix}}}:recurring:due";

	private static RedisKey RecurringDedupeKey(RedisJobStorageOptions options) =>
		$"{{{options.KeyPrefix}}}:recurring:dedupe";

	private static string RecurringDueMember(DateTimeOffset nextRunAt, string name) =>
		string.Create(CultureInfo.InvariantCulture, $"{nextRunAt.UtcTicks:D19}|{name}");

	private static JobRecord CreateJob(string id, DateTimeOffset now) => new()
	{
		Id = id,
		JobName = "test-job",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now,
	};

	private static JobAcquisitionRequest CreateRequest(
		string workerId,
		int capacity,
		string queueName = JobQueueDefinition.DefaultName
	) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = capacity,
		Queues =
		[
			new()
			{
				QueueName = queueName,
				Capacity = capacity,
				JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal)
				{
					["test-job"] = capacity,
				},
			},
		],
	};
}
#pragma warning restore CS1591

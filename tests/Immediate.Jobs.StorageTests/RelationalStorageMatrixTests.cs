using System.Globalization;
using System.Text.RegularExpressions;
using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.LinqToDB;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.StorageTests;

#pragma warning disable CS1591
[Collection(StorageContainerFixtureGroup.Name)]
public sealed class RelationalStorageMatrixTests(StorageContainers containers)
{
	public static TheoryData<DatabaseKind, AdapterKind> Matrix => new()
	{
		{ DatabaseKind.Sqlite, AdapterKind.EntityFrameworkCore },
		{ DatabaseKind.Sqlite, AdapterKind.LinqToDB },
		{ DatabaseKind.PostgreSql, AdapterKind.EntityFrameworkCore },
		{ DatabaseKind.PostgreSql, AdapterKind.LinqToDB },
		{ DatabaseKind.SqlServer, AdapterKind.EntityFrameworkCore },
		{ DatabaseKind.SqlServer, AdapterKind.LinqToDB },
	};

	public static TheoryData<AdapterKind> SqlServerAdapters =>
	[
		AdapterKind.EntityFrameworkCore,
		AdapterKind.LinqToDB,
	];

	public static TheoryData<DatabaseKind> LinqToDbDatabases =>
	[
		DatabaseKind.Sqlite,
		DatabaseKind.PostgreSql,
		DatabaseKind.SqlServer,
	];

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterPassesCoreStorageConformance(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob("parent", now) with { Context = "{\"tenant\":\"matrix\"}", BatchId = "batch" };
		var child = CreateJob("child", now) with
		{
			BatchId = "batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(new()
		{
			BatchId = "batch",
			CreatedAt = now,
			TotalJobs = 2,
			PendingCount = 2,
			State = BatchState.Executing,
		}, [parent, child], [new()
		{
			ChildJobId = child.JobId,
			ParentJobId = parent.JobId,
			Trigger = ContinuationTrigger.Success,
		}], cancellationToken);

		var acquiredParent = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		Assert.Equal(parent.JobId, acquiredParent.JobId);
		Assert.Equal(parent.Context, acquiredParent.Context);
		var executionStartedAt = now.AddSeconds(1);
		await storage.SetExecutionTelemetryAsync(
			parent.JobId,
			1, "worker",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			executionStartedAt,
			cancellationToken
		);
		var correlated = Assert.Single(await storage.QueryJobsAsync(new() { Id = parent.JobId }, cancellationToken));
		Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", correlated.ExecutionTraceId);
		Assert.Equal("00f067aa0ba902b7", correlated.ExecutionSpanId);
		Assert.Equal(executionStartedAt, correlated.ExecutionStartedAt);
		var activeExecution = Assert.Single(await storage.QueryJobExecutionsAsync(
			new() { JobId = parent.JobId },
			cancellationToken
		));
		Assert.Equal(JobExecutionState.Active, activeExecution.State);
		Assert.Equal("worker", activeExecution.WorkerId);
		Assert.Equal(executionStartedAt, activeExecution.ExecutionStartedAt);
		await storage.CompleteAsync(parent.JobId, 1, "worker", cancellationToken);
		var completedExecution = Assert.Single(await storage.QueryJobExecutionsAsync(
			new() { JobId = parent.JobId },
			cancellationToken
		));
		Assert.Equal(JobExecutionState.Succeeded, completedExecution.State);
		_ = Assert.NotNull(completedExecution.CompletedAt);
		var acquiredChild = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		Assert.Equal(child.JobId, acquiredChild.JobId);
		await storage.CompleteAsync(child.JobId, 1, "worker", cancellationToken);
		var status = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(BatchState.Succeeded, status.State);
		Assert.Equal(2, status.Succeeded);
		var childExecution = Assert.Single(await storage.QueryJobExecutionsAsync(
			new() { JobId = child.JobId },
			cancellationToken
		));
		Assert.Equal(JobExecutionState.Succeeded, childExecution.State);

		await storage.DeleteBatchAsync("batch", cancellationToken);
		Assert.Empty(await storage.QueryJobExecutionsAsync(new() { JobId = parent.JobId }, cancellationToken));
		Assert.Empty(await storage.QueryJobExecutionsAsync(new() { JobId = child.JobId }, cancellationToken));
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task CancelBatchCancelsMembersWithUnsettledDependencyChain(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob("parent", now) with { BatchId = "batch" };
		var child = CreateJob("child", now) with
		{
			BatchId = "batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(new()
		{
			BatchId = "batch",
			CreatedAt = now,
			TotalJobs = 2,
			PendingCount = 2,
			State = BatchState.Executing,
		}, [parent, child], [new()
		{
			ChildJobId = child.JobId,
			ParentJobId = parent.JobId,
			Trigger = ContinuationTrigger.Success,
		}], cancellationToken);

		await storage.CancelBatchAsync("batch", cancellationToken);

		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(parent.JobId, cancellationToken))!.State);
		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State);
		var status = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(BatchState.Cancelled, status.State);
		Assert.Equal(2, status.Cancelled);
		Assert.Equal(0, status.Remaining);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterHandlesContentionAndLeaseRecovery(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		foreach (var index in Enumerable.Range(0, 24))
			await first.EnqueueAsync(CreateJob(string.Create(CultureInfo.InvariantCulture, $"contended-{index}"), now), cancellationToken);

		var claims = await Task.WhenAll(
			first.AcquireDueJobsAsync(CreateRequest("node-a", 24), cancellationToken).AsTask(),
			second.AcquireDueJobsAsync(CreateRequest("node-b", 24), cancellationToken).AsTask()
		);
		var claimed = claims.SelectMany(static claim => claim).ToArray();
		Assert.Equal(24, claimed.Select(job => job.JobId).Distinct().Count());
		foreach (var job in claimed)
			await first.CompleteAsync(job.JobId, job.Attempt, job.WorkerId!, cancellationToken);

		var leased = CreateJob("leased", now.AddMinutes(1));
		await first.EnqueueAsync(leased, cancellationToken);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
		var original = Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		await first.SetExecutionTelemetryAsync(
			leased.JobId,
			1,
			"node-a",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			fixture.TimeProvider.GetUtcNow(),
			cancellationToken
		);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
		var recovered = Assert.Single(await second.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		Assert.Equal("leased", recovered.JobId);
		Assert.Equal(2, recovered.Attempt);
		Assert.Equal("node-a", recovered.WorkerId);
		Assert.Null(recovered.ExecutionTraceId);
		Assert.Null(recovered.ExecutionSpanId);
		Assert.Null(recovered.ExecutionStartedAt);

		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => first.RenewLeaseAsync(leased.JobId, original.Attempt, "node-a", TimeSpan.FromMinutes(1), cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => first.SetExecutionTelemetryAsync(
			leased.JobId,
			original.Attempt,
			"node-a",
			"stale",
			"stale",
			fixture.TimeProvider.GetUtcNow(),
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => first.CompleteAsync(leased.JobId, original.Attempt, "node-a", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => first.FailAsync(
			leased.JobId,
			original.Attempt,
			"node-a",
			"stale",
			nextRetryAt: null,
			cancellationToken
		).AsTask());

		var executions = await first.QueryJobExecutionsAsync(new() { JobId = leased.JobId }, cancellationToken);
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
				Assert.Equal(original.LeaseExpiresAt, execution.CompletedAt);
				Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", execution.ExecutionTraceId);
			}
		);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task ReclaimedLeaseRejectsStaleCompletionAndItsBufferedContinuation(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var firstGraph = Assert.IsAssignableFrom<IJobGraphStorage>(first);
		var now = fixture.TimeProvider.GetUtcNow();
		await first.EnqueueAsync(CreateJob("reclaimed-parent", now), cancellationToken);
		_ = Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
		var reclaimed = Assert.Single(await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));

		var exception = await Assert.ThrowsAsync<ImmediateJobException>(() =>
			firstGraph.CompleteWithContinuationsAsync(
				"reclaimed-parent",
				1, "node-a",
				[new()
				{
					Job = CreateJob("stale-continuation", fixture.TimeProvider.GetUtcNow()),
					Options = ContinuationOptions.Detached,
				}],
				cancellationToken
			).AsTask()
		);

		Assert.Contains("does not own active job", exception.Message, StringComparison.Ordinal);
		Assert.Empty(await first.QueryJobsAsync(new() { Id = "stale-continuation" }, cancellationToken));
		Assert.Equal("node-b", reclaimed.WorkerId);
		Assert.Equal(JobState.Active, (await first.GetJobStatusAsync("reclaimed-parent", cancellationToken))!.State);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task ConcurrentLegacyRetriesTreatSyntheticExecutionInsertAsARace(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var first = fixture.CreateStorage();
		var second = fixture.CreateStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await first.EnqueueAsync(CreateJob("legacy-retry", now) with
		{
			State = JobState.Failed,
			Attempt = 1,
			CompletedAt = now,
			LastError = "legacy failure",
		}, cancellationToken);
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		async Task<Exception?> RetryAsync(IJobStorage storage)
		{
			await start.Task;
			return await Record.ExceptionAsync(() => storage.RetryAsync("legacy-retry", cancellationToken).AsTask());
		}

		var retries = new[] { RetryAsync(first), RetryAsync(second) };
		start.SetResult();
		var exceptions = await Task.WhenAll(retries);

		_ = Assert.Single(exceptions, static exception => exception is null);
		_ = Assert.IsType<ImmediateJobException>(Assert.Single(exceptions.OfType<Exception>()));
		var execution = Assert.Single(await first.QueryJobExecutionsAsync(
			new() { JobId = "legacy-retry" },
			cancellationToken
		));
		Assert.True(execution.IsSynthetic);
	}

	[Theory]
	[MemberData(nameof(LinqToDbDatabases))]
	public async Task LinqCompletionSurvivesSustainedBatchHeaderContention(DatabaseKind database)
	{
		const int JobCount = 24;
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(
			database,
			AdapterKind.LinqToDB,
			cancellationToken
		);
		var storage = fixture.GraphStorage;
		var replica = Assert.IsAssignableFrom<IJobStorageReplica>(storage);
		var now = fixture.TimeProvider.GetUtcNow();
		var jobs = Enumerable.Range(0, JobCount)
			.Select(index =>
			{
				var id = string.Create(CultureInfo.InvariantCulture, $"contended-completion-{index}");
				return CreateJob(id, now) with { BatchId = "contended-batch" };
			})
			.ToArray();
		await storage.EnqueueBatchAsync(new()
		{
			BatchId = "contended-batch",
			CreatedAt = now,
			TotalJobs = JobCount,
			PendingCount = JobCount,
			State = BatchState.Executing,
		}, jobs, [], cancellationToken);
		Assert.Equal(JobCount, (await replica.AcquireJobsAsync(
			[.. jobs.Select(static job => job.JobId)],
			"worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		)).Count);
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var completions = jobs.Select(async job =>
		{
			await start.Task;
			await storage.CompleteAsync(job.JobId, 1, "worker", cancellationToken);
		}).ToArray();

		start.SetResult();
		await Task.WhenAll(completions);

		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("contended-batch", cancellationToken));
		Assert.Equal(BatchState.Succeeded, batch.State);
		Assert.Equal(JobCount, batch.Succeeded);
	}

	[Theory]
	[MemberData(nameof(LinqToDbDatabases))]
	public async Task LinqRecurringUpsertSurvivesConcurrentMaterialization(DatabaseKind database)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(
			database,
			AdapterKind.LinqToDB,
			cancellationToken
		);
		var storage = fixture.RecurringStorage;
		var now = fixture.TimeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "racing-upsert",
			JobName = "matrix-test",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = false,
			NextRunAt = now,
		};
		await storage.UpsertRecurringAsync(schedule, cancellationToken);

		for (var iteration = 0; iteration < 20; iteration++)
		{
			var current = Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
			var requested = schedule with
			{
				Cron = iteration % 2 == 0 ? "15 * * * *" : "45 * * * *",
				NextRunAt = now.AddDays(iteration + 1),
			};
			var occurrence = CreateJob(
				string.Create(CultureInfo.InvariantCulture, $"racing-occurrence-{iteration}"),
				now
			) with
			{
				RecurringKey = string.Create(
					CultureInfo.InvariantCulture,
					$"racing-upsert:{current.NextRunAt.UtcTicks}"
				),
			};
			var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			async Task MaterializeAsync()
			{
				await start.Task;
				_ = await storage.MaterializeRecurringAsync(
					current,
					occurrence,
					current.NextRunAt.AddMinutes(1),
					cancellationToken
				);
			}

			async Task UpsertAsync()
			{
				await start.Task;
				await storage.UpsertRecurringAsync(requested, cancellationToken);
			}

			var materialization = MaterializeAsync();
			var upsert = UpsertAsync();

			start.SetResult();
			await Task.WhenAll(materialization, upsert);

			var persisted = Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring);
			Assert.Equal(requested.Cron, persisted.Cron);
			Assert.Equal(requested.NextRunAt, persisted.NextRunAt);
		}
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterPagesJobsWithTiedCreationTimesExactlyOnce(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.Storage;
		var now = fixture.TimeProvider.GetUtcNow();
		foreach (var index in Enumerable.Range(0, 30).Reverse())
			await storage.EnqueueAsync(CreateJob(string.Create(CultureInfo.InvariantCulture, $"tied-{index:d2}"), now), cancellationToken);

		var seen = new List<string>();
		for (var skip = 0; skip < 30; skip += 7)
		{
			var page = await storage.QueryJobsAsync(new() { Skip = skip, Take = 7 }, cancellationToken);
			seen.AddRange(page.Select(static job => job.JobId));
		}

		Assert.Equal(
			Enumerable.Range(0, 30).Select(static index => string.Create(CultureInfo.InvariantCulture, $"tied-{index:d2}")),
			seen
		);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterRotatesToAGroupThatArrivesAfterEarlierService(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.Storage;
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(
			CreateJob("group-a-first", now) with { GroupId = "group-a" },
			cancellationToken
		);
		await storage.EnqueueAsync(
			CreateJob("group-a-second", now) with
			{
				GroupId = "group-a",
				CreatedAt = now.AddTicks(1),
			},
			cancellationToken
		);

		var request = CreateRequest("fair-worker", 1) with
		{
			FairQueues = new FairQueuePolicy { ConcurrencyShareThreshold = 0.10, MinInflightForNoisy = 30, GroupRoundRobin = true },
		};
		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("group-a-first", first.JobId);
		await storage.CompleteAsync(first.JobId, 1, "fair-worker", cancellationToken);
		await storage.EnqueueAsync(
			CreateJob("group-b-first", now) with
			{
				GroupId = "group-b",
				CreatedAt = now.AddTicks(2),
			},
			cancellationToken
		);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("group-b-first", second.JobId);
		Assert.Equal("group-b", second.GroupId);
	}

	[Theory]
	[MemberData(nameof(SqlServerAdapters))]
	public async Task SqlServerCollationIsUsedConsistentlyForGroupState(AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(
			DatabaseKind.SqlServer,
			adapter,
			cancellationToken
		);
		var storage = fixture.Storage;
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.EnqueueAsync(
			CreateJob("tenant-first", now) with { GroupId = "Tenant" },
			cancellationToken
		);
		await storage.EnqueueAsync(
			CreateJob("tenant-second", now) with
			{
				GroupId = "tenant",
				CreatedAt = now.AddTicks(1),
			},
			cancellationToken
		);
		var request = CreateRequest("fair-worker", 1) with
		{
			FairQueues = new FairQueuePolicy { ConcurrencyShareThreshold = 0.10, MinInflightForNoisy = 30, GroupRoundRobin = true },
		};
		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("tenant-first", first.JobId);
		await storage.CompleteAsync(first.JobId, 1, "fair-worker", cancellationToken);
		await storage.EnqueueAsync(
			CreateJob("quiet-first", now) with
			{
				GroupId = "quiet",
				CreatedAt = now.AddTicks(2),
			},
			cancellationToken
		);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));

		Assert.Equal("quiet-first", second.JobId);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterDeduplicatesRecurringAndRollsBackInvalidGraphs(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.RecurringStorage;
		var graphStorage = fixture.GraphStorage;
		var now = fixture.TimeProvider.GetUtcNow();
		var schedule = new RecurringJobSchedule
		{
			Name = "recurring",
			JobName = "matrix-test",
			Cron = "0 * * * *",
			TimeZone = "UTC",
			IsCodeDefined = true,
			NextRunAt = now,
		};
		var occurrence = CreateJob("occurrence", now) with { RecurringKey = string.Create(CultureInfo.InvariantCulture, $"recurring:{now.UtcTicks}") };
		await storage.UpsertRecurringAsync(schedule, cancellationToken);
		Assert.True(await storage.MaterializeRecurringAsync(schedule, occurrence, now.AddHours(1), cancellationToken));
		Assert.False(await storage.MaterializeRecurringAsync(schedule, occurrence, now.AddHours(1), cancellationToken));
		Assert.Equal("recurring", Assert.Single((await storage.GetMonitoringSnapshotAsync(cancellationToken)).Recurring).Name);

		var invalid = CreateJob("invalid-child", now) with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		_ = await Assert.ThrowsAnyAsync<Exception>(() => graphStorage.EnqueueContinuationAsync(
			invalid,
			[new() { ChildJobId = invalid.JobId, ParentJobId = "missing" }],
			cancellationToken
		).AsTask());
		Assert.Null(await storage.GetJobStatusAsync(invalid.JobId, cancellationToken));
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterDropsServersWithoutARecentHeartbeat(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.Storage;
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.HeartbeatAsync(new JobServerSnapshot { WorkerId = "node-a", LastHeartbeat = now, ActiveWorkers = 1, MaxWorkers = 8 }, cancellationToken);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(3));
		await storage.HeartbeatAsync(new JobServerSnapshot { WorkerId = "node-b", LastHeartbeat = fixture.TimeProvider.GetUtcNow(), ActiveWorkers = 2, MaxWorkers = 8 }, cancellationToken);

		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);

		Assert.Equal("node-b", Assert.Single(snapshot.Servers).WorkerId);
		Assert.Equal(1, await fixture.CountServersAsync(cancellationToken));
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task BulkIncomingEdgesPreserveFieldsAndNormalizeInput(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var now = fixture.TimeProvider.GetUtcNow();
		var batchParent = CreateJob("batch-parent-member", now) with
		{
			BatchId = "parent-batch",
			State = JobState.Succeeded,
			CompletedAt = now,
		};
		await storage.EnqueueBatchAsync(new()
		{
			BatchId = "parent-batch",
			CreatedAt = now,
			TotalJobs = 1,
			PendingCount = 0,
			SucceededCount = 1,
			State = BatchState.Succeeded,
		}, [batchParent], [], cancellationToken);
		var jobParent = CreateJob("job-parent", now.AddTicks(1));
		await storage.EnqueueAsync(jobParent, cancellationToken);
		var firstChild = CreateJob("first-child", now.AddTicks(2));
		await storage.EnqueueContinuationAsync(firstChild,
		[
			new()
			{
				ChildJobId = firstChild.JobId,
				ParentJobId = jobParent.JobId,
				Trigger = ContinuationTrigger.Success,
			},
			new()
			{
				ChildJobId = firstChild.JobId,
				ParentBatchId = "parent-batch",
				Trigger = ContinuationTrigger.Complete,
			},
		], cancellationToken);
		var secondChild = CreateJob("second-child", now.AddTicks(3));
		await storage.EnqueueContinuationAsync(secondChild, [new()
		{
			ChildJobId = secondChild.JobId,
			ParentJobId = jobParent.JobId,
			Trigger = ContinuationTrigger.Failure,
		}], cancellationToken);

		var edges = await storage.GetIncomingEdgesAsync(
			[secondChild.JobId, firstChild.JobId, firstChild.JobId, "missing"],
			cancellationToken
		);

		Assert.Collection(
			edges.OrderBy(static edge => edge.ChildJobId)
				.ThenBy(static edge => edge.ParentBatchId is null ? 0 : 1)
				.ThenBy(static edge => edge.ParentJobId ?? edge.ParentBatchId),
			edge =>
			{
				Assert.Equal(firstChild.JobId, edge.ChildJobId);
				Assert.Equal(jobParent.JobId, edge.ParentJobId);
				Assert.Null(edge.ParentBatchId);
				Assert.Equal(ContinuationTrigger.Success, edge.Trigger);
			},
			edge =>
			{
				Assert.Equal(firstChild.JobId, edge.ChildJobId);
				Assert.Null(edge.ParentJobId);
				Assert.Equal("parent-batch", edge.ParentBatchId);
				Assert.Equal(ContinuationTrigger.Complete, edge.Trigger);
			},
			edge =>
			{
				Assert.Equal(secondChild.JobId, edge.ChildJobId);
				Assert.Equal(jobParent.JobId, edge.ParentJobId);
				Assert.Null(edge.ParentBatchId);
				Assert.Equal(ContinuationTrigger.Failure, edge.Trigger);
			}
		);
		Assert.Empty(await storage.GetIncomingEdgesAsync([], cancellationToken));
		_ = await Assert.ThrowsAsync<ArgumentException>(() =>
			storage.GetIncomingEdgesAsync([" "], cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			storage.GetIncomingEdgesAsync(null!, cancellationToken).AsTask()
		);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task MixedTriggersUseEveryIncomingEdgeRegardlessOfCompletionOrder(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var replica = Assert.IsType<IJobStorageReplica>(storage, exactMatch: false);
		var now = fixture.TimeProvider.GetUtcNow();
		var scenarios = new[]
		{
			(FailureParentSettlesFirst: true, FailureParentFails: false, ExpectedState: JobState.Skipped),
			(FailureParentSettlesFirst: false, FailureParentFails: false, ExpectedState: JobState.Skipped),
			(FailureParentSettlesFirst: true, FailureParentFails: true, ExpectedState: JobState.Pending),
			(FailureParentSettlesFirst: false, FailureParentFails: true, ExpectedState: JobState.Pending),
		};
		for (var index = 0; index < scenarios.Length; index++)
		{
			var (failureParentSettlesFirst, failureParentFails, expectedState) = scenarios[index];
			var successParent = CreateJob(string.Create(CultureInfo.InvariantCulture, $"success-parent-{index}"), now.AddTicks(index * 3)) with { DueAt = now };
			var failureParent = CreateJob(string.Create(CultureInfo.InvariantCulture, $"failure-parent-{index}"), now.AddTicks((index * 3) + 1)) with { DueAt = now };
			var child = CreateJob(string.Create(CultureInfo.InvariantCulture, $"mixed-child-{index}"), now.AddTicks((index * 3) + 2)) with { DueAt = now };
			await storage.EnqueueAsync(successParent, cancellationToken);
			await storage.EnqueueAsync(failureParent, cancellationToken);
			await storage.EnqueueContinuationAsync(child,
			[
				new()
				{
					ChildJobId = child.JobId,
					ParentJobId = successParent.JobId,
					Trigger = ContinuationTrigger.Success,
				},
				new()
				{
					ChildJobId = child.JobId,
					ParentJobId = failureParent.JobId,
					Trigger = ContinuationTrigger.Failure,
				},
			], cancellationToken);
			_ = Assert.Single(await replica.AcquireJobsAsync(
				[successParent.JobId],
				"worker",
				TimeSpan.FromMinutes(1),
				cancellationToken
			));
			_ = Assert.Single(await replica.AcquireJobsAsync(
				[failureParent.JobId],
				"worker",
				TimeSpan.FromMinutes(1),
				cancellationToken
			));

			async Task SettleSuccessParentAsync() =>
				await storage.CompleteAsync(successParent.JobId, 1, "worker", cancellationToken);
			async Task SettleFailureParentAsync()
			{
				if (failureParentFails)
				{
					await storage.FailAsync(
						failureParent.JobId,
						1, "worker",
						"expected",
						nextRetryAt: null,
						cancellationToken
					);
				}
				else
				{
					await storage.CompleteAsync(failureParent.JobId, 1, "worker", cancellationToken);
				}
			}

			if (failureParentSettlesFirst)
			{
				await SettleFailureParentAsync();
				Assert.Equal(
					JobState.AwaitingContinuation,
					(await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State
				);
				await SettleSuccessParentAsync();
			}
			else
			{
				await SettleSuccessParentAsync();
				Assert.Equal(
					JobState.AwaitingContinuation,
					(await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State
				);
				await SettleFailureParentAsync();
			}

			Assert.Equal(
				expectedState,
				(await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State
			);
		}
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task RetriedParentDoesNotSettleContinuationEdgeTwice(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var replica = Assert.IsAssignableFrom<IJobStorageReplica>(storage);
		var now = fixture.TimeProvider.GetUtcNow();
		var retriedParent = CreateJob("retried-parent", now);
		var otherParent = CreateJob("other-parent", now.AddTicks(1)) with { DueAt = now };
		var child = CreateJob("retry-child", now.AddTicks(2)) with { DueAt = now };
		await storage.EnqueueAsync(retriedParent, cancellationToken);
		await storage.EnqueueAsync(otherParent, cancellationToken);
		await storage.EnqueueContinuationAsync(child,
		[
			new()
			{
				ChildJobId = child.JobId,
				ParentJobId = retriedParent.JobId,
				Trigger = ContinuationTrigger.Complete,
			},
			new()
			{
				ChildJobId = child.JobId,
				ParentJobId = otherParent.JobId,
				Trigger = ContinuationTrigger.Complete,
			},
		], cancellationToken);
		_ = Assert.Single(await replica.AcquireJobsAsync(
			[retriedParent.JobId],
			"worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		));
		await storage.FailAsync(retriedParent.JobId, 1, "worker", "expected", nextRetryAt: null, cancellationToken);
		await storage.RetryAsync(retriedParent.JobId, cancellationToken);
		_ = Assert.Single(await replica.AcquireJobsAsync(
			[retriedParent.JobId],
			"worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		));

		await storage.CompleteAsync(retriedParent.JobId, 2, "worker", cancellationToken);

		Assert.Equal(
			JobState.AwaitingContinuation,
			(await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State
		);
		_ = Assert.Single(await replica.AcquireJobsAsync(
			[otherParent.JobId],
			"worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		));
		await storage.CompleteAsync(otherParent.JobId, 1, "worker", cancellationToken);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task ConcurrentCompletionAndContinuationInsertCannotStrandChild(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var replica = Assert.IsAssignableFrom<IJobStorageReplica>(storage);
		var now = fixture.TimeProvider.GetUtcNow();
		for (var index = 0; index < 20; index++)
		{
			var parentId = string.Create(CultureInfo.InvariantCulture, $"racing-parent-{index}");
			var childId = string.Create(CultureInfo.InvariantCulture, $"racing-child-{index}");
			var parent = CreateJob(parentId, now.AddTicks(index * 2)) with { DueAt = now };
			var child = CreateJob(childId, now.AddTicks((index * 2) + 1)) with { DueAt = now };
			await storage.EnqueueAsync(parent, cancellationToken);
			_ = Assert.Single(await replica.AcquireJobsAsync(
				[parent.JobId],
				"worker",
				TimeSpan.FromMinutes(1),
				cancellationToken
			));
			var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var completion = Task.Run(async () =>
			{
				await start.Task;
				await storage.CompleteAsync(parent.JobId, 1, "worker", cancellationToken);
			}, cancellationToken);
			var insertion = Task.Run(async () =>
			{
				await start.Task;
				await storage.EnqueueContinuationAsync(child, [new()
				{
					ChildJobId = child.JobId,
					ParentJobId = parent.JobId,
					Trigger = ContinuationTrigger.Complete,
				}], cancellationToken);
			}, cancellationToken);

			start.SetResult();
			await Task.WhenAll(completion, insertion);

			Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(child.JobId, cancellationToken))!.State);
		}
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task DynamicAdditionsRejectInvalidBatchIdsBeforeMutation(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var replica = Assert.IsType<IJobStorageReplica>(storage, exactMatch: false);
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob("dynamic-current", now) with { BatchId = "dynamic-batch" };
		await storage.EnqueueBatchAsync(new()
		{
			BatchId = "dynamic-batch",
			CreatedAt = now,
			TotalJobs = 1,
			PendingCount = 1,
			State = BatchState.Executing,
		}, [current], [], cancellationToken);
		_ = Assert.Single(await replica.AcquireJobsAsync(
			[current.JobId],
			"worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		));

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.JobId,
			1, "worker",
			[new()
			{
				Job = CreateJob("detached-with-batch", now.AddTicks(1)) with { BatchId = "dynamic-batch" },
				Options = ContinuationOptions.Detached,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.JobId,
			1, "worker",
			[new()
			{
				Job = CreateJob("tracked-with-wrong-batch", now.AddTicks(2)) with { BatchId = "other-batch" },
				Options = ContinuationOptions.BesideContinuations,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.AddBatchJobAsync(
			current.JobId,
			1,
			CreateJob("added-with-wrong-batch", now.AddTicks(3)) with { BatchId = "other-batch" },
			ContinuationOptions.BesideContinuations,
			cancellationToken
		).AsTask());

		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("dynamic-batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.JobId, cancellationToken))!.State);
		foreach (var id in new[] { "detached-with-batch", "tracked-with-wrong-batch", "added-with-wrong-batch" })
			Assert.Empty(await storage.QueryJobsAsync(new() { Id = id }, cancellationToken));
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task DeletingStandaloneContinuationRemovesIncomingEdges(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob("delete-parent", now) with { State = JobState.Succeeded, CompletedAt = now };
		var child = CreateJob("delete-child", now) with { State = JobState.Succeeded, CompletedAt = now };
		await storage.EnqueueAsync(parent, cancellationToken);
		await storage.EnqueueContinuationAsync(child, [new()
		{
			ChildJobId = child.JobId,
			ParentJobId = parent.JobId,
			Trigger = ContinuationTrigger.Success,
		}], cancellationToken);

		_ = Assert.Single(await storage.GetIncomingEdgesAsync([child.JobId], cancellationToken));
		await storage.DeleteAsync(child.JobId, cancellationToken);

		Assert.Null(await storage.GetJobStatusAsync(child.JobId, cancellationToken));
		Assert.Empty(await storage.GetIncomingEdgesAsync([child.JobId], cancellationToken));
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task RetryFastForwardsScheduledJobs(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.Storage;
		var now = fixture.TimeProvider.GetUtcNow();
		var job = CreateJob("scheduled-retry", now) with
		{
			State = JobState.Scheduled,
			DueAt = now.AddHours(1),
			Attempt = 1,
			LastError = "expected failure",
		};
		await storage.EnqueueAsync(job, cancellationToken);

		await storage.RetryAsync(job.JobId, cancellationToken);

		var retried = Assert.Single(await storage.QueryJobsAsync(new() { Id = job.JobId }, cancellationToken));
		Assert.Equal(JobState.Pending, retried.State);
		Assert.Equal(now, retried.DueAt);
		Assert.Equal(1, retried.Attempt);
		Assert.Equal(job.LastError, retried.LastError);

		var firstRun = job with { JobId = "scheduled-first-run", Attempt = 0, LastError = null };
		await storage.EnqueueAsync(firstRun, cancellationToken);
		await storage.RetryAsync(firstRun.JobId, cancellationToken);
		var fastForwarded = Assert.Single(await storage.QueryJobsAsync(new() { Id = firstRun.JobId }, cancellationToken));
		Assert.Equal(JobState.Pending, fastForwarded.State);
		Assert.Equal(now, fastForwarded.DueAt);
		Assert.Equal(0, fastForwarded.Attempt);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task CancelActiveJobClosesExecutionAndFencesWorker(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.Storage;
		var job = CreateJob("cancel-active", fixture.TimeProvider.GetUtcNow());
		await storage.EnqueueAsync(job, cancellationToken);
		var active = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));

		await storage.CancelAsync(job.JobId, cancellationToken);

		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(job.JobId, cancellationToken))!.State);
		var execution = Assert.Single(await storage.QueryJobExecutionsAsync(new() { JobId = job.JobId }, cancellationToken));
		Assert.Equal(JobExecutionState.Cancelled, execution.State);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.CompleteAsync(job.JobId, active.Attempt, "worker", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<ImmediateJobException>(
			() => storage.CancelAsync(job.JobId, cancellationToken).AsTask()
		);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task CancelBatchMemberPropagatesContinuations(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob("cancel-parent", now) with { BatchId = "cancel-member-batch" };
		var successChild = CreateJob("cancel-success-child", now) with
		{
			BatchId = "cancel-member-batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		var completeChild = CreateJob("cancel-complete-child", now) with
		{
			BatchId = "cancel-member-batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(new()
		{
			BatchId = "cancel-member-batch",
			CreatedAt = now,
			TotalJobs = 3,
			PendingCount = 3,
			State = BatchState.Executing,
		}, [parent, successChild, completeChild],
		[
			new()
			{
				ChildJobId = successChild.JobId,
				ParentJobId = parent.JobId,
				Trigger = ContinuationTrigger.Success,
			},
			new()
			{
				ChildJobId = completeChild.JobId,
				ParentJobId = parent.JobId,
				Trigger = ContinuationTrigger.Complete,
			},
		], cancellationToken);

		await storage.CancelAsync(parent.JobId, cancellationToken);

		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(parent.JobId, cancellationToken))!.State);
		Assert.Equal(JobState.Skipped, (await storage.GetJobStatusAsync(successChild.JobId, cancellationToken))!.State);
		Assert.Equal(JobState.Pending, (await storage.GetJobStatusAsync(completeChild.JobId, cancellationToken))!.State);
		var status = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("cancel-member-batch", cancellationToken));
		Assert.Equal(BatchState.Executing, status.State);
		Assert.Equal(1, status.Cancelled);
		Assert.Equal(1, status.Skipped);
		Assert.Equal(1, status.Remaining);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task SkippedFailureBranchDoesNotCancelSuccessfulBatch(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.GraphStorage;
		var now = fixture.TimeProvider.GetUtcNow();
		var parent = CreateJob("successful-parent", now) with { BatchId = "skipped-branch-batch" };
		var failureOnly = CreateJob("failure-only", now) with
		{
			BatchId = "skipped-branch-batch",
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueBatchAsync(new()
		{
			BatchId = "skipped-branch-batch",
			CreatedAt = now,
			TotalJobs = 2,
			PendingCount = 2,
			State = BatchState.Executing,
		}, [parent, failureOnly], [new()
		{
			ChildJobId = failureOnly.JobId,
			ParentJobId = parent.JobId,
			Trigger = ContinuationTrigger.Failure,
		}], cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));

		await storage.CompleteAsync(parent.JobId, 1, "worker", cancellationToken);

		Assert.Equal(JobState.Skipped, (await storage.GetJobStatusAsync(failureOnly.JobId, cancellationToken))!.State);
		var status = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("skipped-branch-batch", cancellationToken));
		Assert.Equal(BatchState.Succeeded, status.State);
		Assert.Equal(1, status.Succeeded);
		Assert.Equal(1, status.Skipped);
		Assert.Equal(0, status.Cancelled);
		Assert.Equal(0, status.Remaining);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task EmptyBatchProgressIsFullySettled(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		await fixture.CreateEmptyBatchAsync("empty-batch", cancellationToken);

		var status = Assert.IsType<BatchStatus>(await fixture.GraphStorage.GetBatchStatusAsync(
			"empty-batch",
			cancellationToken
		));

		Assert.Equal(0, status.Total);
		Assert.Equal(0, status.Remaining);
		Assert.Equal(1d, status.FractionSettled);
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task MissingDashboardActionsThrowKeyNotFoundException(
		DatabaseKind database,
		AdapterKind adapter
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.Storage;
		var graphStorage = Assert.IsType<IJobGraphStorage>(storage, exactMatch: false);
		var recurringStorage = Assert.IsType<IRecurringJobStorage>(storage, exactMatch: false);

		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.CancelAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.RetryAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => storage.DeleteAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => recurringStorage.RemoveRecurringAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => recurringStorage.PauseRecurringAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => recurringStorage.ResumeRecurringAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => graphStorage.CancelBatchAsync("missing", cancellationToken).AsTask()
		);
		_ = await Assert.ThrowsAsync<KeyNotFoundException>(
			() => graphStorage.DeleteBatchAsync("missing", cancellationToken).AsTask()
		);
	}

	[Theory]
	[InlineData(DatabaseKind.Sqlite)]
	[InlineData(DatabaseKind.PostgreSql)]
	[InlineData(DatabaseKind.SqlServer)]
	public async Task AdaptersShareLinqToDBCreatedSchema(DatabaseKind database)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, AdapterKind.LinqToDB, cancellationToken);
		var efStorage = fixture.CreateEntityFrameworkCoreStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await fixture.Storage.EnqueueAsync(CreateJob("from-linq2db", now), cancellationToken);
		Assert.Equal("from-linq2db", Assert.Single(await efStorage.QueryJobsAsync(
			new() { Id = "from-linq2db" },
			cancellationToken
		)).JobId);
		await efStorage.EnqueueAsync(CreateJob("from-ef", now.AddTicks(1)), cancellationToken);
		Assert.Equal("from-ef", Assert.Single(await fixture.Storage.QueryJobsAsync(
			new() { Id = "from-ef" },
			cancellationToken
		)).JobId);
	}

	[Theory]
	[InlineData(DatabaseKind.Sqlite)]
	[InlineData(DatabaseKind.PostgreSql)]
	[InlineData(DatabaseKind.SqlServer)]
	public async Task AdaptersShareEntityFrameworkCoreCreatedSchema(DatabaseKind database)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, AdapterKind.EntityFrameworkCore, cancellationToken);
		var linqStorage = fixture.CreateLinqToDBStorage();
		var now = fixture.TimeProvider.GetUtcNow();
		await fixture.Storage.EnqueueAsync(CreateJob("from-ef", now), cancellationToken);
		Assert.Equal("from-ef", Assert.Single(await linqStorage.QueryJobsAsync(
			new() { Id = "from-ef" },
			cancellationToken
		)).JobId);
		await linqStorage.EnqueueAsync(CreateJob("from-linq2db", now.AddTicks(1)), cancellationToken);
		Assert.Equal("from-linq2db", Assert.Single(await fixture.Storage.QueryJobsAsync(
			new() { Id = "from-linq2db" },
			cancellationToken
		)).JobId);
	}

	private async Task<MatrixFixture> CreateFixtureAsync(
		DatabaseKind database,
		AdapterKind adapter,
		CancellationToken cancellationToken
	)
	{
		var schema = database == DatabaseKind.Sqlite ? null : "jobs_" + Guid.NewGuid().ToString("N");
		var sqlitePath = database == DatabaseKind.Sqlite
			? Path.Combine(Path.GetTempPath(), $"immediate-jobs-matrix-{Guid.NewGuid():N}.db")
			: null;
		var connectionString = database switch
		{
			DatabaseKind.Sqlite => $"Data Source={sqlitePath}",
			DatabaseKind.PostgreSql => containers.PostgreSql.GetConnectionString(),
			DatabaseKind.SqlServer => containers.SqlServer.GetConnectionString(),
			_ => throw new ArgumentOutOfRangeException(nameof(database)),
		};
		var contextOptions = new DbContextOptionsBuilder<MatrixDbContext>();
		DataOptions dataOptions;
		if (database == DatabaseKind.Sqlite)
		{
			dataOptions = new DataOptions().UseSQLite(connectionString);
			_ = contextOptions.UseSqlite(connectionString);
		}
		else if (database == DatabaseKind.PostgreSql)
		{
			dataOptions = new DataOptions().UsePostgreSQL(connectionString);
			_ = contextOptions.UseNpgsql(connectionString);
		}
		else
		{
			dataOptions = new DataOptions().UseSqlServer(connectionString);
			_ = contextOptions.UseSqlServer(connectionString);
		}

		_ = contextOptions.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();
		var contextFactory = new MatrixDbContextFactory(contextOptions.Options, schema);
		var fixture = new MatrixFixture(
			database,
			connectionString,
			schema,
			sqlitePath,
			dataOptions,
			contextFactory,
			adapter
		);
		try
		{
			if (adapter == AdapterKind.LinqToDB)
			{
				await dataOptions.CreateImmediateJobsSchemaAsync(schema, cancellationToken);
			}
			else
			{
				await using var context = contextFactory.CreateDbContext();
				var script = context.Database.GenerateCreateScript();
				foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
				{
					if (!string.IsNullOrWhiteSpace(batch))
						_ = await context.Database.ExecuteSqlRawAsync(batch, cancellationToken);
				}
			}

			return fixture;
		}
		catch
		{
			await fixture.DisposeAsync();
			throw;
		}
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
			JobCapacities = new Dictionary<string, int> { ["matrix-test"] = batchSize },
		}],
	};

	private static JobRecord CreateJob(string id, DateTimeOffset now) => new()
	{
		JobId = id,
		JobName = "matrix-test",
		Payload = "{}",
		State = JobState.Pending,
		DueAt = now,
		CreatedAt = now,
	};

	public enum DatabaseKind
	{
		Sqlite,
		PostgreSql,
		SqlServer,
	}

	public enum AdapterKind
	{
		EntityFrameworkCore,
		LinqToDB,
	}

	private sealed class MatrixFixture(
		DatabaseKind database,
		string connectionString,
		string? schema,
		string? sqlitePath,
		DataOptions dataOptions,
		MatrixDbContextFactory contextFactory,
		AdapterKind adapter
	) : IAsyncDisposable
	{
		private readonly List<IJobStorage> _storages = [];

		public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
		public IJobStorage Storage => adapter == AdapterKind.LinqToDB
			? CreateLinqToDBStorage()
			: CreateEntityFrameworkCoreStorage();
		public IRecurringJobStorage RecurringStorage => (IRecurringJobStorage)Storage;
		public IJobGraphStorage GraphStorage => (IJobGraphStorage)Storage;

		public IJobStorage CreateStorage() => Storage;

		public async ValueTask CreateEmptyBatchAsync(string batchId, CancellationToken cancellationToken)
		{
			var storage = GraphStorage;
			var now = TimeProvider.GetUtcNow();
			var member = CreateJob($"{batchId}-member", now) with
			{
				BatchId = batchId,
				State = JobState.Succeeded,
				CompletedAt = now,
			};
			await storage.EnqueueBatchAsync(new()
			{
				BatchId = batchId,
				CreatedAt = now,
				TotalJobs = 1,
				PendingCount = 0,
				SucceededCount = 1,
				State = BatchState.Succeeded,
			}, [member], [], cancellationToken);

			var tablePrefix = database switch
			{
				DatabaseKind.Sqlite => string.Empty,
				DatabaseKind.PostgreSql => $"\"{schema}\".",
				DatabaseKind.SqlServer => $"[{schema}].",
				_ => throw new InvalidOperationException($"Unknown matrix database '{database}'."),
			};
			var batchTable = tablePrefix + (database == DatabaseKind.SqlServer
				? "[immediate_job_batches]"
				: "\"immediate_job_batches\"");
			var jobTable = tablePrefix + (database == DatabaseKind.SqlServer
				? "[immediate_jobs]"
				: "\"immediate_jobs\"");
			string Quote(string identifier) => database == DatabaseKind.SqlServer
				? $"[{identifier}]"
				: $"\"{identifier}\"";
			await using var connection = new DataConnection(dataOptions);
			_ = await connection.ExecuteAsync(
				$"DELETE FROM {jobTable} WHERE {Quote("BatchId")} = '{batchId}'",
				cancellationToken
			);
			_ = await connection.ExecuteAsync(
				$"""
				UPDATE {batchTable}
				SET {Quote("TotalJobs")} = 0,
					{Quote("PendingCount")} = 0,
					{Quote("SucceededCount")} = 0,
					{Quote("FailedCount")} = 0,
					{Quote("CancelledCount")} = 0,
					{Quote("SkippedCount")} = 0
				WHERE {Quote("Id")} = '{batchId}'
				""",
				cancellationToken
			);
		}

		public async ValueTask<int> CountServersAsync(CancellationToken cancellationToken)
		{
			var tablePrefix = database switch
			{
				DatabaseKind.Sqlite => string.Empty,
				DatabaseKind.PostgreSql => $"\"{schema}\".",
				DatabaseKind.SqlServer => $"[{schema}].",
				_ => throw new InvalidOperationException($"Unknown matrix database '{database}'."),
			};
			var serverTable = tablePrefix + (database == DatabaseKind.SqlServer
				? "[immediate_job_servers]"
				: "\"immediate_job_servers\"");
			await using var connection = new DataConnection(dataOptions);
			return await connection.ExecuteAsync<int>(
				$"SELECT COUNT(*) FROM {serverTable}",
				cancellationToken
			);
		}

		public EntityFrameworkCoreJobStorage<MatrixDbContext> CreateEntityFrameworkCoreStorage()
		{
			var storage = new EntityFrameworkCoreJobStorage<MatrixDbContext>(contextFactory, TimeProvider);
			_storages.Add(storage);
			return storage;
		}

		public LinqToDBJobStorage CreateLinqToDBStorage()
		{
			var storage = new LinqToDBJobStorage(dataOptions, schema, TimeProvider);
			_storages.Add(storage);
			return storage;
		}

		public async ValueTask DisposeAsync()
		{
			foreach (var storage in _storages.AsEnumerable().Reverse())
				await storage.DisposeAsync();

			if (sqlitePath is not null)
			{
				SqliteConnection.ClearAllPools();
				File.Delete(sqlitePath);
				return;
			}

			var cleanupOptions = database == DatabaseKind.PostgreSql
				? new DataOptions().UsePostgreSQL(connectionString)
				: new DataOptions().UseSqlServer(connectionString);
			await using var connection = new DataConnection(cleanupOptions);
			if (database == DatabaseKind.PostgreSql)
			{
				_ = await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
			}
			else
			{
				foreach (var table in new[]
				{
					"immediate_job_continuations",
					"immediate_job_executions",
					"immediate_fair_queue_groups",
					"immediate_jobs",
					"immediate_job_batches",
					"immediate_recurring_jobs",
					"immediate_job_servers",
				})
				{
					_ = await connection.ExecuteAsync($"DROP TABLE IF EXISTS [{schema}].[{table}]");
				}

				_ = await connection.ExecuteAsync(
					$"IF SCHEMA_ID(N'{schema}') IS NOT NULL EXEC(N'DROP SCHEMA [{schema}]')"
				);
			}
		}

	}

	private sealed class MatrixDbContextFactory(
		DbContextOptions<MatrixDbContext> options,
		string? schema
	) : IDbContextFactory<MatrixDbContext>
	{
		public MatrixDbContext CreateDbContext() => new(options, schema);
	}

	private sealed class MatrixDbContext(DbContextOptions<MatrixDbContext> options, string? schema)
		: DbContext(options)
	{
		public string? Schema { get; } = schema;

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			_ = modelBuilder.AddImmediateJobs(Schema);
		}
	}

	private sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
	{
		public object Create(DbContext context, bool designTime) =>
			(context.GetType(), ((MatrixDbContext)context).Schema, designTime);
	}
}
#pragma warning restore CS1591

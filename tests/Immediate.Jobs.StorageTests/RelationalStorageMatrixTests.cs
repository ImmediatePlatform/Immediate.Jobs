using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.LinqToDB;
using global::LinqToDB;
using global::LinqToDB.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using System.Text.RegularExpressions;

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
			Id = "batch",
			CreatedAt = now,
			TotalJobs = 2,
			PendingCount = 2,
			State = BatchState.Executing,
		}, [parent, child], [new()
		{
			ChildJobId = child.Id,
			ParentJobId = parent.Id,
			Trigger = ContinuationTrigger.Success,
		}], cancellationToken);

		var acquiredParent = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		Assert.Equal(parent.Id, acquiredParent.Id);
		Assert.Equal(parent.Context, acquiredParent.Context);
		var executionStartedAt = now.AddSeconds(1);
		await storage.SetExecutionTelemetryAsync(
			parent.Id,
			"worker",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			executionStartedAt,
			cancellationToken
		);
		var correlated = Assert.Single(await storage.QueryJobsAsync(new() { Id = parent.Id }, cancellationToken));
		Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", correlated.ExecutionTraceId);
		Assert.Equal("00f067aa0ba902b7", correlated.ExecutionSpanId);
		Assert.Equal(executionStartedAt, correlated.ExecutionStartedAt);
		await storage.CompleteAsync(parent.Id, "worker", cancellationToken);
		var acquiredChild = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker", 1), cancellationToken));
		Assert.Equal(child.Id, acquiredChild.Id);
		await storage.CompleteAsync(child.Id, "worker", cancellationToken);
		var status = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("batch", cancellationToken));
		Assert.Equal(BatchState.Succeeded, status.State);
		Assert.Equal(2, status.Succeeded);
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
			Id = "batch",
			CreatedAt = now,
			TotalJobs = 2,
			PendingCount = 2,
			State = BatchState.Executing,
		}, [parent, child], [new()
		{
			ChildJobId = child.Id,
			ParentJobId = parent.Id,
			Trigger = ContinuationTrigger.Success,
		}], cancellationToken);

		await storage.CancelBatchAsync("batch", cancellationToken);

		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(parent.Id, cancellationToken))!.State);
		Assert.Equal(JobState.Cancelled, (await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State);
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
			await first.EnqueueAsync(CreateJob($"contended-{index}", now), cancellationToken);

		var claims = await Task.WhenAll(
			first.AcquireDueJobsAsync(CreateRequest("node-a", 24), cancellationToken).AsTask(),
			second.AcquireDueJobsAsync(CreateRequest("node-b", 24), cancellationToken).AsTask()
		);
		var claimed = claims.SelectMany(static claim => claim).ToArray();
		Assert.Equal(24, claimed.Select(job => job.Id).Distinct().Count());
		foreach (var job in claimed)
			await first.CompleteAsync(job.Id, job.WorkerId!, cancellationToken);

		var leased = CreateJob("leased", now.AddMinutes(1));
		await first.EnqueueAsync(leased, cancellationToken);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
		_ = Assert.Single(await first.AcquireDueJobsAsync(CreateRequest("node-a", 1), cancellationToken));
		await first.SetExecutionTelemetryAsync(
			leased.Id,
			"node-a",
			"4bf92f3577b34da6a3ce929d0e0e4736",
			"00f067aa0ba902b7",
			fixture.TimeProvider.GetUtcNow(),
			cancellationToken
		);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));
		var recovered = Assert.Single(await second.AcquireDueJobsAsync(CreateRequest("node-b", 1), cancellationToken));
		Assert.Equal("leased", recovered.Id);
		Assert.Equal(2, recovered.Attempt);
		Assert.Null(recovered.ExecutionTraceId);
		Assert.Null(recovered.ExecutionSpanId);
		Assert.Null(recovered.ExecutionStartedAt);
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
			await storage.EnqueueAsync(CreateJob($"tied-{index:d2}", now), cancellationToken);

		var seen = new List<string>();
		for (var skip = 0; skip < 30; skip += 7)
		{
			var page = await storage.QueryJobsAsync(new() { Skip = skip, Take = 7 }, cancellationToken);
			seen.AddRange(page.Select(static job => job.Id));
		}

		Assert.Equal(
			Enumerable.Range(0, 30).Select(static index => $"tied-{index:d2}"),
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
			FairQueues = new(0.10, 30, true),
		};
		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("group-a-first", first.Id);
		await storage.CompleteAsync(first.Id, "fair-worker", cancellationToken);
		await storage.EnqueueAsync(
			CreateJob("group-b-first", now) with
			{
				GroupId = "group-b",
				CreatedAt = now.AddTicks(2),
			},
			cancellationToken
		);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("group-b-first", second.Id);
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
			FairQueues = new(0.10, 30, true),
		};
		var first = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));
		Assert.Equal("tenant-first", first.Id);
		await storage.CompleteAsync(first.Id, "fair-worker", cancellationToken);
		await storage.EnqueueAsync(
			CreateJob("quiet-first", now) with
			{
				GroupId = "quiet",
				CreatedAt = now.AddTicks(2),
			},
			cancellationToken
		);

		var second = Assert.Single(await storage.AcquireDueJobsAsync(request, cancellationToken));

		Assert.Equal("quiet-first", second.Id);
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
		var occurrence = CreateJob("occurrence", now) with { RecurringKey = $"recurring:{now.UtcTicks}" };
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
			[new() { ChildJobId = invalid.Id, ParentJobId = "missing" }],
			cancellationToken
		).AsTask());
		Assert.Null(await storage.GetJobStatusAsync(invalid.Id, cancellationToken));
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public async Task AdapterDropsServersWithoutARecentHeartbeat(DatabaseKind database, AdapterKind adapter)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var fixture = await CreateFixtureAsync(database, adapter, cancellationToken);
		var storage = fixture.Storage;
		var now = fixture.TimeProvider.GetUtcNow();
		await storage.HeartbeatAsync(new("node-a", now, 1, 8), cancellationToken);
		fixture.TimeProvider.Advance(TimeSpan.FromMinutes(3));
		await storage.HeartbeatAsync(new("node-b", fixture.TimeProvider.GetUtcNow(), 2, 8), cancellationToken);

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
			Id = "parent-batch",
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
				ChildJobId = firstChild.Id,
				ParentJobId = jobParent.Id,
				Trigger = ContinuationTrigger.Success,
			},
			new()
			{
				ChildJobId = firstChild.Id,
				ParentBatchId = "parent-batch",
				Trigger = ContinuationTrigger.Complete,
			},
		], cancellationToken);
		var secondChild = CreateJob("second-child", now.AddTicks(3));
		await storage.EnqueueContinuationAsync(secondChild, [new()
		{
			ChildJobId = secondChild.Id,
			ParentJobId = jobParent.Id,
			Trigger = ContinuationTrigger.Failure,
		}], cancellationToken);

		var edges = await storage.GetIncomingEdgesAsync(
			[secondChild.Id, firstChild.Id, firstChild.Id, "missing"],
			cancellationToken
		);

		Assert.Collection(
			edges.OrderBy(static edge => edge.ChildJobId)
				.ThenBy(static edge => edge.ParentBatchId is null ? 0 : 1)
				.ThenBy(static edge => edge.ParentJobId ?? edge.ParentBatchId),
			edge =>
			{
				Assert.Equal(firstChild.Id, edge.ChildJobId);
				Assert.Equal(jobParent.Id, edge.ParentJobId);
				Assert.Null(edge.ParentBatchId);
				Assert.Equal(ContinuationTrigger.Success, edge.Trigger);
			},
			edge =>
			{
				Assert.Equal(firstChild.Id, edge.ChildJobId);
				Assert.Null(edge.ParentJobId);
				Assert.Equal("parent-batch", edge.ParentBatchId);
				Assert.Equal(ContinuationTrigger.Complete, edge.Trigger);
			},
			edge =>
			{
				Assert.Equal(secondChild.Id, edge.ChildJobId);
				Assert.Equal(jobParent.Id, edge.ParentJobId);
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
		var replica = Assert.IsAssignableFrom<IJobStorageReplica>(storage);
		var now = fixture.TimeProvider.GetUtcNow();
		var scenarios = new[]
		{
			(FailureParentSettlesFirst: true, FailureParentFails: false, ExpectedState: JobState.Cancelled),
			(FailureParentSettlesFirst: false, FailureParentFails: false, ExpectedState: JobState.Cancelled),
			(FailureParentSettlesFirst: true, FailureParentFails: true, ExpectedState: JobState.Pending),
			(FailureParentSettlesFirst: false, FailureParentFails: true, ExpectedState: JobState.Pending),
		};
		for (var index = 0; index < scenarios.Length; index++)
		{
			var (failureParentSettlesFirst, failureParentFails, expectedState) = scenarios[index];
			var successParent = CreateJob($"success-parent-{index}", now.AddTicks(index * 3)) with { DueAt = now };
			var failureParent = CreateJob($"failure-parent-{index}", now.AddTicks(index * 3 + 1)) with { DueAt = now };
			var child = CreateJob($"mixed-child-{index}", now.AddTicks(index * 3 + 2)) with { DueAt = now };
			await storage.EnqueueAsync(successParent, cancellationToken);
			await storage.EnqueueAsync(failureParent, cancellationToken);
			await storage.EnqueueContinuationAsync(child,
			[
				new()
				{
					ChildJobId = child.Id,
					ParentJobId = successParent.Id,
					Trigger = ContinuationTrigger.Success,
				},
				new()
				{
					ChildJobId = child.Id,
					ParentJobId = failureParent.Id,
					Trigger = ContinuationTrigger.Failure,
				},
			], cancellationToken);
			_ = Assert.Single(await replica.AcquireJobsAsync(
				[successParent.Id],
				"worker",
				TimeSpan.FromMinutes(1),
				cancellationToken
			));
			_ = Assert.Single(await replica.AcquireJobsAsync(
				[failureParent.Id],
				"worker",
				TimeSpan.FromMinutes(1),
				cancellationToken
			));

			async Task SettleSuccessParentAsync() =>
				await storage.CompleteAsync(successParent.Id, "worker", cancellationToken);
			async Task SettleFailureParentAsync()
			{
				if (failureParentFails)
				{
					await storage.FailAsync(
						failureParent.Id,
						"worker",
						"expected",
						nextRetryAt: null,
						cancellationToken
					);
				}
				else
				{
					await storage.CompleteAsync(failureParent.Id, "worker", cancellationToken);
				}
			}

			if (failureParentSettlesFirst)
			{
				await SettleFailureParentAsync();
				Assert.Equal(
					JobState.AwaitingContinuation,
					(await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State
				);
				await SettleSuccessParentAsync();
			}
			else
			{
				await SettleSuccessParentAsync();
				Assert.Equal(
					JobState.AwaitingContinuation,
					(await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State
				);
				await SettleFailureParentAsync();
			}

			Assert.Equal(
				expectedState,
				(await storage.GetJobStatusAsync(child.Id, cancellationToken))!.State
			);
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
		var replica = Assert.IsAssignableFrom<IJobStorageReplica>(storage);
		var now = fixture.TimeProvider.GetUtcNow();
		var current = CreateJob("dynamic-current", now) with { BatchId = "dynamic-batch" };
		await storage.EnqueueBatchAsync(new()
		{
			Id = "dynamic-batch",
			CreatedAt = now,
			TotalJobs = 1,
			PendingCount = 1,
			State = BatchState.Executing,
		}, [current], [], cancellationToken);
		_ = Assert.Single(await replica.AcquireJobsAsync(
			[current.Id],
			"worker",
			TimeSpan.FromMinutes(1),
			cancellationToken
		));

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			"worker",
			[new()
			{
				Job = CreateJob("detached-with-batch", now.AddTicks(1)) with { BatchId = "dynamic-batch" },
				Options = ContinuationOptions.Detached,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			"worker",
			[new()
			{
				Job = CreateJob("tracked-with-wrong-batch", now.AddTicks(2)) with { BatchId = "other-batch" },
				Options = ContinuationOptions.BesideContinuations,
			}],
			cancellationToken
		).AsTask());
		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.AddBatchJobAsync(
			current.Id,
			CreateJob("added-with-wrong-batch", now.AddTicks(3)) with { BatchId = "other-batch" },
			ContinuationOptions.BesideContinuations,
			cancellationToken
		).AsTask());

		var batch = Assert.IsType<BatchStatus>(await storage.GetBatchStatusAsync("dynamic-batch", cancellationToken));
		Assert.Equal(1, batch.Total);
		Assert.Equal(1, batch.Remaining);
		Assert.Equal(JobState.Active, (await storage.GetJobStatusAsync(current.Id, cancellationToken))!.State);
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
			ChildJobId = child.Id,
			ParentJobId = parent.Id,
			Trigger = ContinuationTrigger.Success,
		}], cancellationToken);

		_ = Assert.Single(await storage.GetIncomingEdgesAsync([child.Id], cancellationToken));
		await storage.DeleteAsync(child.Id, cancellationToken);

		Assert.Null(await storage.GetJobStatusAsync(child.Id, cancellationToken));
		Assert.Empty(await storage.GetIncomingEdgesAsync([child.Id], cancellationToken));
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
		var graphStorage = Assert.IsAssignableFrom<IJobGraphStorage>(storage);
		var recurringStorage = Assert.IsAssignableFrom<IRecurringJobStorage>(storage);

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
		)).Id);
		await efStorage.EnqueueAsync(CreateJob("from-ef", now.AddTicks(1)), cancellationToken);
		Assert.Equal("from-ef", Assert.Single(await fixture.Storage.QueryJobsAsync(
			new() { Id = "from-ef" },
			cancellationToken
		)).Id);
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
		)).Id);
		await linqStorage.EnqueueAsync(CreateJob("from-linq2db", now.AddTicks(1)), cancellationToken);
		Assert.Equal("from-linq2db", Assert.Single(await fixture.Storage.QueryJobsAsync(
			new() { Id = "from-linq2db" },
			cancellationToken
		)).Id);
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
				await dataOptions.CreateImmediateJobsSchemaAsync(schema, cancellationToken);
			}
			else
			{
				await using var context = contextFactory.CreateDbContext();
				var script = context.Database.GenerateCreateScript();
				foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
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
		Id = id,
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
				Id = batchId,
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
			await using var connection = new global::LinqToDB.Data.DataConnection(dataOptions);
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
					{Quote("CancelledCount")} = 0
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
			await using var connection = new global::LinqToDB.Data.DataConnection(dataOptions);
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
			await using var connection = new global::LinqToDB.Data.DataConnection(cleanupOptions);
			if (database == DatabaseKind.PostgreSql)
			{
				_ = await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
			}
			else
			{
				foreach (var table in new[]
				{
					"immediate_job_continuations",
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

using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Immediate.Jobs.FunctionalTests.Storage;

public sealed class InMemoryJobStorageBatchTests
{

	[Fact]
	public async Task AddingBatchJobReportsStaleAndActiveExecutionNumbers()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var current = CreateJob("parent", batchId: "batch");
		await storage.EnqueueBatchAsync(CreateBatch("batch", 1), [current], [], cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		clock.Advance(TimeSpan.FromMinutes(2));
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));

		var exception = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.AddBatchJobAsync(
			current.Id,
			1,
			CreateJob("child", batchId: "batch"),
			ContinuationOptions.BesideContinuations,
			cancellationToken
		).AsTask());

		Assert.Contains("Execution 1", exception.Message, StringComparison.Ordinal);
		Assert.Contains("active execution is 2", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TerminalParentIsEvaluatedWhenContinuationIsInserted()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		await using var storage = new InMemoryJobStorage(clock);
		var parent = CreateJob("parent");
		await storage.EnqueueAsync(parent, cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		await storage.FailAsync(parent.Id, 1, "worker", "broken", nextRetryAt: null, cancellationToken);

		var successOnly = CreateJob("success-only") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueContinuationAsync(
			successOnly,
			[new() { ChildJobId = successOnly.Id, ParentJobId = parent.Id }],
			cancellationToken
		);
		Assert.Equal(JobState.Skipped, (await GetJobAsync(storage, successOnly.Id, cancellationToken)).State);

		var always = CreateJob("always") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueContinuationAsync(
			always,
			[new()
			{
				ChildJobId = always.Id,
				ParentJobId = parent.Id,
				Trigger = ContinuationTrigger.Complete,
			}],
			cancellationToken
		);
		Assert.Equal(JobState.Pending, (await GetJobAsync(storage, always.Id, cancellationToken)).State);

		var failureOnly = CreateJob("failure-only") with
		{
			State = JobState.AwaitingContinuation,
			RemainingDependencies = 1,
		};
		await storage.EnqueueContinuationAsync(
			failureOnly,
			[new()
			{
				ChildJobId = failureOnly.Id,
				ParentJobId = parent.Id,
				Trigger = ContinuationTrigger.Failure,
			}],
			cancellationToken
		);
		var released = await GetJobAsync(storage, failureOnly.Id, cancellationToken);
		Assert.Equal(JobState.Pending, released.State);
		Assert.Equal(1, released.FailedDependencies);
	}

	[Theory]
	[InlineData(ContinuationOptions.Detached, "other-batch")]
	[InlineData(ContinuationOptions.BesideContinuations, "other-batch")]
	public async Task DynamicContinuationRejectsInvalidBatchRelationship(
		ContinuationOptions options,
		string additionBatchId
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var storage = new InMemoryJobStorage(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
		var current = CreateJob("parent", batchId: "batch");
		await storage.EnqueueBatchAsync(CreateBatch("batch", 1), [current], [], cancellationToken);
		_ = Assert.Single(await storage.AcquireDueJobsAsync(CreateRequest("worker"), cancellationToken));
		var addition = CreateJob("inserted", batchId: additionBatchId);

		_ = await Assert.ThrowsAsync<ImmediateJobException>(() => storage.CompleteWithContinuationsAsync(
			current.Id,
			1, "worker",
			[new() { Job = addition, Options = options }],
			cancellationToken
		).AsTask());

		Assert.Empty(await storage.QueryJobsAsync(new() { Id = addition.Id }, cancellationToken));
		Assert.Equal(JobState.Active, (await GetJobAsync(storage, current.Id, cancellationToken)).State);
		Assert.Equal(1, (await storage.GetBatchStatusAsync("batch", cancellationToken))!.Total);
	}

	private static JobRecord CreateJob(string id, string? batchId = null) => new()
	{
		Id = id,
		JobName = id,
		Payload = "{}",
		State = JobState.Pending,
		DueAt = DateTimeOffset.UnixEpoch,
		CreatedAt = DateTimeOffset.UnixEpoch,
		BatchId = batchId,
	};

	private static BatchRecord CreateBatch(string id, int count) => new()
	{
		Id = id,
		CreatedAt = DateTimeOffset.UnixEpoch,
		TotalJobs = count,
		PendingCount = count,
		State = BatchState.Executing,
	};

	private static JobAcquisitionRequest CreateRequest(string workerId) => new()
	{
		WorkerId = workerId,
		Lease = TimeSpan.FromMinutes(1),
		BatchSize = 10,
		Queues =
		[
			new()
			{
				QueueName = JobQueueDefinition.DefaultName,
				Capacity = 10,
				JobCapacities = new Dictionary<string, int>(StringComparer.Ordinal)
				{
					["parent"] = 10,
					["child"] = 10,
					["always"] = 10,
					["success-only"] = 10,
					["inserted"] = 10,
				},
			},
		],
	};

	private static async ValueTask<JobRecord> GetJobAsync(
		InMemoryJobStorage storage,
		string jobId,
		CancellationToken cancellationToken
	) => Assert.Single(await storage.QueryJobsAsync(new() { Id = jobId }, cancellationToken));
}

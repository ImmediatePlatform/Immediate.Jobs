using Immediate.Jobs.Aspire.Api.Contracts;
using Immediate.Jobs.Aspire.Api.Jobs;
using Immediate.Jobs.Aspire.Api.Workflows;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Endpoints;

public static class SampleApiEndpoints
{
	public static IEndpointRouteBuilder MapSampleApiEndpoints(this IEndpointRouteBuilder endpoints)
	{
		_ = endpoints.MapPost("/api/greetings/{name}", EnqueueGreetingAsync)
			.WithName("EnqueueGreeting")
			.WithSummary("Enqueues a background greeting job")
			.WithDescription(
				"Captures the request IP address and User-Agent, persists the job to PostgreSQL, "
				+ "and restores that context before executing it through Immediate.Handlers."
			)
			.Produces<EnqueueJobResponse>(StatusCodes.Status202Accepted);

		_ = endpoints.MapPost("/api/fair-queue-demo", EnqueueFairQueueDemoAsync)
			.WithName("EnqueueFairQueueDemo")
			.WithSummary("Creates a noisy backlog followed by a quiet fair-queue group")
			.WithDescription(
				"Atomically enqueues 100 deliberately slow jobs in one group and one job in a "
				+ "second group that becomes due five seconds later. The second group's job should "
				+ "advance ahead of the remaining backlog."
			)
			.Produces<FairQueueDemoResponse>(StatusCodes.Status202Accepted);

		_ = endpoints.MapPost("/api/retry-demo", EnqueueRetryDemoAsync)
			.WithName("EnqueueRetryDemo")
			.WithSummary("Enqueues a job that succeeds on its third attempt")
			.WithDescription(
				"The first two attempts fail and schedule a retry five minutes later. Use Run now "
				+ "in the Immediate.Jobs dashboard to fast-forward each scheduled retry."
			)
			.Produces<EnqueueJobResponse>(StatusCodes.Status202Accepted);

		_ = endpoints.MapPost(
				"/api/continuation-branch-demo/{failRoot:bool}",
				CreateContinuationBranchDemoAsync
			)
			.WithName("CreateContinuationBranchDemo")
			.WithSummary("Creates success and failure continuations for one root job")
			.WithDescription(
				"Creates an atomic three-job workflow. Set failRoot to choose whether the success "
				+ "or failure continuation runs; the unselected branch becomes Skipped."
			)
			.Produces<CreateContinuationBranchDemoResponse>(StatusCodes.Status202Accepted);

		_ = endpoints.MapPost("/api/order-fulfillment-batches", CreateOrderBatchAsync)
			.WithName("CreateOrderFulfillmentBatch")
			.WithSummary("Creates a complex order-fulfillment batch")
			.WithDescription(
				"Creates an atomic ten-job workflow with chains, parallel branches, two fan-in joins, "
				+ "a Complete audit continuation, and a retry-safe mid-job expansion that adds an "
				+ "eleventh fraud-assessment job. Open the returned dashboard URL to watch it run."
			)
			.Produces<CreateOrderBatchResponse>(StatusCodes.Status202Accepted);

		_ = endpoints.MapPost("/api/game-release-batches/{title}", CreateGameReleaseBatchAsync)
			.WithName("CreateGameReleaseBatch")
			.WithSummary("Creates a global game-release workflow")
			.WithDescription(
				"Creates an atomic 19-job workflow with repeated fan-out and fan-in diamonds: "
				+ "client and service workstreams split independently, converge into a release "
				+ "candidate, fan out across four distribution tasks, then converge and fan out "
				+ "again for the global launch."
			)
			.Produces<CreateGameReleaseBatchResponse>(StatusCodes.Status202Accepted);

		return endpoints;
	}

	private static async ValueTask<IResult> EnqueueGreetingAsync(
		string name,
		AspireGreetingJob.Scheduler scheduler,
		CancellationToken cancellationToken
	)
	{
		var job = await scheduler.EnqueueAsync(new(name), cancellationToken);
		return Results.Accepted(
			"/jobs",
			new EnqueueJobResponse(job.Value, new Uri("/jobs", UriKind.Relative))
		);
	}

	private static async ValueTask<IResult> EnqueueFairQueueDemoAsync(
		FairQueueDemoJob.Scheduler scheduler,
		BatchScheduler batches,
		CancellationToken cancellationToken
	)
	{
		const int BacklogJobs = 100;
		var runId = Guid.NewGuid();
		var backlogGroup = $"fair-demo:{runId:N}:backlog";
		var quietGroup = $"fair-demo:{runId:N}:quiet";
		await using var batch = batches.Begin();
		for (var sequence = 1; sequence <= BacklogJobs; sequence++)
		{
			scheduler.Enqueue(
				new(runId, sequence, "backlog"),
				batch,
				groupId: backlogGroup
			);
		}

		var quietJob = scheduler.Schedule(
			new(runId, 1, "quiet"),
			batch,
			delay: TimeSpan.FromSeconds(5),
			groupId: quietGroup
		);

		_ = await batch.CommitAsync(cancellationToken);

		return Results.Accepted(
			"/jobs",
			new FairQueueDemoResponse(
				runId,
				BacklogJobs,
				backlogGroup,
				quietGroup,
				quietJob.JobHandle.Value,
				new Uri("/jobs", UriKind.Relative)
			)
		);
	}

	private static async ValueTask<IResult> EnqueueRetryDemoAsync(
		ThirdAttemptSuccessJob.Scheduler scheduler,
		CancellationToken cancellationToken
	)
	{
		var job = await scheduler.EnqueueAsync(new(Guid.NewGuid()), cancellationToken);
		return Results.Accepted(
			"/jobs",
			new EnqueueJobResponse(job.Value, new Uri("/jobs", UriKind.Relative))
		);
	}

	private static async ValueTask<IResult> CreateContinuationBranchDemoAsync(
		bool failRoot,
		BatchScheduler batches,
		ContinuationBranchRootJob.Scheduler rootScheduler,
		ContinuationBranchSuccessJob.Scheduler successScheduler,
		ContinuationBranchFailureJob.Scheduler failureScheduler,
		CancellationToken cancellationToken
	)
	{
		var runId = Guid.NewGuid();
		await using var batch = batches.Begin();

		var root = rootScheduler.Enqueue(new(runId, failRoot), batch);

		var success = successScheduler.ScheduleAfter(
			new(runId),
			root,
			ContinuationTrigger.Success
		);

		var failure = failureScheduler.ScheduleAfter(
			new(runId),
			root,
			ContinuationTrigger.Failure
		);

		var batchHandle = await batch.CommitAsync(cancellationToken);

		return Results.Accepted(
			$"/jobs/api/batches/{batchHandle.Value}",
			new CreateContinuationBranchDemoResponse(
				runId,
				failRoot,
				batchHandle.Value,
				root.JobHandle.Value,
				success.JobHandle.Value,
				failure.JobHandle.Value,
				new Uri("/jobs", UriKind.Relative),
				new Uri($"/jobs/api/batches/{batchHandle.Value}", UriKind.Relative)
			)
		);
	}

	private static async ValueTask<IResult> CreateOrderBatchAsync(
		OrderFulfillmentWorkflow workflow,
		CancellationToken cancellationToken
	)
	{
		var orderId = Guid.NewGuid();
		var batch = await workflow.CreateAsync(orderId, cancellationToken);
		return Results.Accepted(
			$"/jobs/api/batches/{batch.Value}",
			new CreateOrderBatchResponse(
				orderId,
				batch.Value,
				OrderFulfillmentWorkflow.InitialJobCount,
				OrderFulfillmentWorkflow.ExpectedJobCount,
				new Uri("/jobs", UriKind.Relative),
				new Uri($"/jobs/api/batches/{batch.Value}", UriKind.Relative)
			)
		);
	}

	private static async ValueTask<IResult> CreateGameReleaseBatchAsync(
		string title,
		GameReleaseWorkflow workflow,
		CancellationToken cancellationToken
	)
	{
		var releaseId = Guid.NewGuid();
		var batch = await workflow.CreateAsync(releaseId, title, cancellationToken);
		return Results.Accepted(
			$"/jobs/api/batches/{batch.Value}",
			new CreateGameReleaseBatchResponse(
				releaseId,
				title,
				batch.Value,
				GameReleaseWorkflow.JobCount,
				new Uri("/jobs", UriKind.Relative),
				new Uri($"/jobs/api/batches/{batch.Value}", UriKind.Relative)
			)
		);
	}
}

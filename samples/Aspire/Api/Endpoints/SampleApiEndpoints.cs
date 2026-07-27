using Immediate.Jobs.Aspire.Api.Contracts;
using Immediate.Jobs.Aspire.Api.Jobs;
using Immediate.Jobs.Aspire.Api.Workflows;

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
				"Enqueues 100 deliberately slow jobs in one group, waits five seconds, then "
				+ "enqueues one job in a second group. The second group's job should advance "
				+ "ahead of the remaining backlog."
			)
			.Produces<FairQueueDemoResponse>(StatusCodes.Status202Accepted);

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
			new EnqueueJobResponse(job.Id, new Uri("/jobs", UriKind.Relative))
		);
	}

	private static async ValueTask<IResult> EnqueueFairQueueDemoAsync(
		FairQueueDemoJob.Scheduler scheduler,
		CancellationToken cancellationToken
	)
	{
		const int BacklogJobs = 100;
		var runId = Guid.NewGuid();
		var backlogGroup = $"fair-demo:{runId:N}:backlog";
		var quietGroup = $"fair-demo:{runId:N}:quiet";
		for (var sequence = 1; sequence <= BacklogJobs; sequence++)
		{
			_ = await scheduler.EnqueueAsync(
				new(runId, sequence, "backlog"),
				cancellationToken,
				groupId: backlogGroup
			);
		}

		await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
		var quietJob = await scheduler.EnqueueAsync(
			new(runId, 1, "quiet"),
			cancellationToken,
			groupId: quietGroup
		);

		return Results.Accepted(
			"/jobs",
			new FairQueueDemoResponse(
				runId,
				BacklogJobs,
				backlogGroup,
				quietGroup,
				quietJob.Id,
				new Uri("/jobs", UriKind.Relative)
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
			$"/jobs/api/batches/{batch.Id}",
			new CreateOrderBatchResponse(
				orderId,
				batch.Id,
				OrderFulfillmentWorkflow.InitialJobCount,
				OrderFulfillmentWorkflow.ExpectedJobCount,
				new Uri("/jobs", UriKind.Relative),
				new Uri($"/jobs/api/batches/{batch.Id}", UriKind.Relative)
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
			$"/jobs/api/batches/{batch.Id}",
			new CreateGameReleaseBatchResponse(
				releaseId,
				title,
				batch.Id,
				GameReleaseWorkflow.JobCount,
				new Uri("/jobs", UriKind.Relative),
				new Uri($"/jobs/api/batches/{batch.Id}", UriKind.Relative)
			)
		);
	}
}

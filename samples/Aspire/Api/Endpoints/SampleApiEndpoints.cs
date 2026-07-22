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

		_ = endpoints.MapPost("/api/order-fulfillment-batches", CreateOrderBatchAsync)
			.WithName("CreateOrderFulfillmentBatch")
			.WithSummary("Creates a complex order-fulfillment batch")
			.WithDescription(
				"Creates an atomic ten-job workflow with chains, parallel branches, two fan-in joins, "
				+ "an AllComplete audit continuation, and a retry-safe mid-job expansion that adds an "
				+ "eleventh fraud-assessment job. Open the returned dashboard URL to watch it run."
			)
			.Produces<CreateOrderBatchResponse>(StatusCodes.Status202Accepted);

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
}

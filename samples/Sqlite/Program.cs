using System.Globalization;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Dashboard;
using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.Shared;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SqliteSample;
using SqliteSample.Jobs;
using SqliteSample.Workflows;

var builder = WebApplication.CreateBuilder(args);

var databasePath = Path.Combine(builder.Environment.ContentRootPath, "immediate-jobs.db");
builder.Services.AddDbContextFactory<JobsDbContext>(options =>
	options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<GameReleaseWorkflow>();
builder.Services.AddScoped<OrderFulfillmentWorkflow>();

builder.Services.AddSqliteSampleHandlers();
builder.Services.AddSqliteSampleJobs()
	.UseFairQueues()
	.ConfigureStorage(o => o
		.UseEntityFrameworkCore<JobsDbContext>()
		.UseSingleServer())
	.ConfigureWorkers(o => o.WorkerCount = 4)
	.AddHealthCheck()
	.AddImmediateJobsDashboard();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
	var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JobsDbContext>>();
	await using var dbContext = await dbContextFactory.CreateDbContextAsync();
	_ = await dbContext.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
	_ = app.MapSwagger("/openapi/{documentName}.json");
	_ = app.MapScalarApiReference(options => options
		.WithTitle("Immediate.Jobs SQLite sample")
		.DisableAgent());
}

app.MapGet("/", () => Results.Redirect("/scalar"))
	.ExcludeFromDescription();

app.MapPost("/greetings/{name}", async (
	string name,
	SendGreetingJob.Scheduler scheduler,
	CancellationToken cancellationToken
) =>
{
	var jobHandle = await scheduler.EnqueueAsync(new(name), cancellationToken);
	return Results.Accepted($"/jobs/invocations/{jobHandle.Value}", new { jobHandle = jobHandle.Value });
})
	.WithSummary("Enqueues a greeting job");

app.MapPost("/greetings/{name}/delayed", async (
	string name,
	SendGreetingJob.Scheduler scheduler,
	CancellationToken cancellationToken
) =>
{
	var jobHandle = await scheduler.ScheduleAsync(new(name), TimeSpan.FromMinutes(1), cancellationToken);
	return Results.Accepted($"/jobs/invocations/{jobHandle.Value}", new { jobHandle = jobHandle.Value });
})
	.WithSummary("Schedules a greeting job to run in one minute");

app.MapPost("/retry-demo", async (
	FlakyJob.Scheduler scheduler,
	CancellationToken cancellationToken
) =>
{
	var jobHandle = await scheduler.EnqueueAsync(new(Guid.NewGuid()), cancellationToken);
	return Results.Accepted($"/jobs/invocations/{jobHandle.Value}", new { jobHandle = jobHandle.Value });
})
	.WithSummary("Enqueues a job that succeeds on its third attempt");

app.MapPost("/fair-queue-demo", async (
	FairQueueDemoJob.Scheduler scheduler,
	BatchScheduler batches,
	CancellationToken cancellationToken
) =>
{
	const int BacklogJobs = 100;
	var runId = Guid.NewGuid();
	var backlogGroup = $"fair-demo:{runId:N}:backlog";
	var quietGroup = $"fair-demo:{runId:N}:quiet";

	await using var batch = batches.Begin();
	for (var sequence = 1; sequence <= BacklogJobs; sequence++)
		_ = scheduler.Enqueue(new(runId, sequence, "backlog"), batch, groupId: backlogGroup);

	var quietJob = scheduler.Schedule(
		new(runId, 1, "quiet"),
		batch,
		delay: TimeSpan.FromSeconds(5),
		groupId: quietGroup
	);
	_ = await batch.CommitAsync(cancellationToken);

	return Results.Accepted(
		$"/jobs/invocations/{quietJob.JobHandle.Value}",
		new { runId, backlogJobs = BacklogJobs, backlogGroup, quietGroup, quietJobHandle = quietJob.JobHandle.Value }
	);
})
	.WithSummary("Creates a noisy backlog followed by a quiet fair-queue group");

app.MapPost("/continuation-branch-demo/{failRoot:bool}", async (
	bool failRoot,
	BatchScheduler batches,
	ContinuationBranchRootJob.Scheduler rootScheduler,
	ContinuationBranchSuccessJob.Scheduler successScheduler,
	ContinuationBranchFailureJob.Scheduler failureScheduler,
	CancellationToken cancellationToken
) =>
{
	var runId = Guid.NewGuid();
	await using var batch = batches.Begin();

	var root = rootScheduler.Enqueue(new(runId, failRoot), batch);
	_ = successScheduler.ScheduleAfter(new(runId), root, ContinuationTrigger.Success);
	_ = failureScheduler.ScheduleAfter(new(runId), root, ContinuationTrigger.Failure);

	var batchHandle = await batch.CommitAsync(cancellationToken);
	return Results.Accepted($"/jobs/batches/{batchHandle.Value}", new { runId, batchHandle = batchHandle.Value });
})
	.WithSummary("Creates success and failure continuations for one root job");

app.MapPost("/order-fulfillment-batches", async (
	OrderFulfillmentWorkflow workflow,
	CancellationToken cancellationToken
) =>
{
	var orderId = Guid.NewGuid();
	var batchHandle = await workflow.CreateAsync(orderId, cancellationToken);
	return Results.Accepted($"/jobs/batches/{batchHandle.Value}", new { orderId, batchHandle = batchHandle.Value });
})
	.WithSummary("Creates an eleven-job order-fulfillment batch");

app.MapPost("/game-release-batches/{title}", async (
	string title,
	GameReleaseWorkflow workflow,
	CancellationToken cancellationToken
) =>
{
	var releaseId = Guid.NewGuid();
	var batchHandle = await workflow.CreateAsync(releaseId, title, cancellationToken);
	return Results.Accepted($"/jobs/batches/{batchHandle.Value}", new { releaseId, batchHandle = batchHandle.Value });
})
	.WithSummary("Creates a 19-job game-release batch");

app.MapHealthChecks("/health");
app.MapImmediateJobsDashboard("/jobs");
await app.RunAsync();

namespace SqliteSample
{
	public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
	{
		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			base.OnModelCreating(modelBuilder);
			_ = modelBuilder.AddImmediateJobs();
		}
	}

	[Handler, Job(Name = "send-greeting", MaxAttempts = 3)]
	public sealed partial class SendGreetingJob(ILogger<SendGreetingJob> logger)
	{
		public sealed record Payload(string Name);

		private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			logger.LogInformation("Hello, {Name}!", payload.Name);
			return ValueTask.CompletedTask;
		}
	}

	[Handler, Job(
		Name = "flaky-demo",
		MaxAttempts = 3,
		Backoff = BackoffStrategy.Fixed,
		BackoffBase = "00:00:10"
	)]
	public sealed partial class FlakyJob(ILogger<FlakyJob> logger)
	{
		public sealed record Payload(Guid RunId) : IJobRequest
		{
			public JobDetails? JobDetails { get; set; }
		}

		private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var attempt = payload.JobDetails?.Attempt ?? 1;
			if (attempt < 3)
				throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Retry demo {payload.RunId} failed attempt {attempt} on purpose."));

			logger.LogInformation("Retry demo {RunId} succeeded on attempt {Attempt}", payload.RunId, attempt);
			return ValueTask.CompletedTask;
		}
	}

	[Handler, Job(Name = "heartbeat", Cron = "0 * * * * *")]
	public sealed partial class HeartbeatJob(ILogger<HeartbeatJob> logger, TimeProvider timeProvider)
	{
		private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			logger.LogInformation("Heartbeat {JobHandle} fired at {FiredAt}", request.JobDetails?.JobHandle, timeProvider.GetUtcNow());
			return ValueTask.CompletedTask;
		}
	}
}

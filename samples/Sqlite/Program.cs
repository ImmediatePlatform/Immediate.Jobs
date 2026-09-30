using System.Globalization;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Dashboard;
using Immediate.Jobs.EntityFrameworkCore;
using Immediate.Jobs.Shared;
using Microsoft.EntityFrameworkCore;
using SqliteSample;

var builder = WebApplication.CreateBuilder(args);

var databasePath = Path.Combine(builder.Environment.ContentRootPath, "immediate-jobs.db");
builder.Services.AddDbContextFactory<JobsDbContext>(options =>
	options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddSqliteSampleHandlers();
builder.Services.AddSqliteSampleJobs()
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

app.MapGet("/", () => Results.Redirect("/jobs"));

app.MapPost("/greetings/{name}", async (
	string name,
	SendGreetingJob.Scheduler scheduler,
	CancellationToken cancellationToken
) =>
{
	var jobHandle = await scheduler.EnqueueAsync(new(name), cancellationToken);
	return Results.Accepted($"/jobs/invocations/{jobHandle.Value}", new { jobHandle = jobHandle.Value });
});

app.MapPost("/greetings/{name}/delayed", async (
	string name,
	SendGreetingJob.Scheduler scheduler,
	CancellationToken cancellationToken
) =>
{
	var jobHandle = await scheduler.ScheduleAsync(new(name), TimeSpan.FromMinutes(1), cancellationToken);
	return Results.Accepted($"/jobs/invocations/{jobHandle.Value}", new { jobHandle = jobHandle.Value });
});

app.MapPost("/retry-demo", async (
	FlakyJob.Scheduler scheduler,
	CancellationToken cancellationToken
) =>
{
	var jobHandle = await scheduler.EnqueueAsync(new(Guid.NewGuid()), cancellationToken);
	return Results.Accepted($"/jobs/invocations/{jobHandle.Value}", new { jobHandle = jobHandle.Value });
});

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

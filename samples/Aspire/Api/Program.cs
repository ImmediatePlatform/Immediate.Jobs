using Immediate.Handlers.Shared;
using Immediate.Jobs;
using Immediate.Jobs.Dashboard;
using Immediate.Jobs.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentRequestContext>();

var connectionString = builder.Configuration.GetConnectionString("jobs")
	?? throw new InvalidOperationException("The Aspire 'jobs' connection string is required.");

builder.Services.AddDbContextFactory<JobsDbContext>(options =>
	options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));

builder.Services.AddImmediateJobs(options =>
{
	options.UseEntityFrameworkCore<JobsDbContext>();
	options.UseSingleServer(); // Explicit; EF storage selects single-server mode implicitly when omitted.
	options.MaxParallelJobs = 4;
	options.PollingInterval = TimeSpan.FromSeconds(5);
}).AddHealthCheck();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
	var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JobsDbContext>>();
	await using var dbContext = await dbContextFactory.CreateDbContextAsync();
	await dbContext.Database.EnsureCreatedAsync();
}

app.MapDefaultEndpoints();
app.MapImmediateJobsDashboard("/jobs");

if (app.Environment.IsDevelopment())
{
	app.MapSwagger("/openapi/{documentName}.json");
	app.MapScalarApiReference(options => options
		.WithTitle("Immediate.Jobs Aspire sample")
		.DisableAgent());
}

app.MapGet("/", () => Results.Redirect("/scalar"))
	.ExcludeFromDescription();

app.MapPost("/api/greetings/{name}", async (
	string name,
	AspireGreetingJob.Scheduler scheduler,
	CancellationToken cancellationToken
) =>
{
	var jobId = await scheduler.Enqueue(new(name), cancellationToken);
	return Results.Accepted("/jobs", new EnqueueJobResponse(jobId, "/jobs"));
})
	.WithName("EnqueueGreeting")
	.WithSummary("Enqueues a background greeting job")
	.WithDescription("Captures the request IP address and User-Agent, persists the job to PostgreSQL, and restores that context before executing it through Immediate.Handlers.")
	.Produces<EnqueueJobResponse>(StatusCodes.Status202Accepted);

await app.RunAsync();

public sealed record EnqueueJobResponse(Guid JobId, string DashboardUrl);

public sealed record OriginatingRequestContext(string ClientIpAddress, string UserAgent);

public sealed class CurrentRequestContext
{
	public OriginatingRequestContext? Value { get; set; }
}

public sealed class RequestContextExtractor(
	IHttpContextAccessor httpContextAccessor,
	CurrentRequestContext currentRequestContext
) : IJobContextExtractor<OriginatingRequestContext>
{
	public string Key => "http-request";

	public ValueTask<OriginatingRequestContext?> CaptureAsync(CancellationToken cancellationToken)
	{
		if (httpContextAccessor.HttpContext is not { } httpContext)
			return ValueTask.FromResult<OriginatingRequestContext?>(null);

		var context = new OriginatingRequestContext(
			httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
			httpContext.Request.Headers["User-Agent"].ToString()
		);
		return ValueTask.FromResult<OriginatingRequestContext?>(context);
	}

	public ValueTask RestoreAsync(
		OriginatingRequestContext context,
		CancellationToken cancellationToken
	)
	{
		currentRequestContext.Value = context;
		return ValueTask.CompletedTask;
	}
}

[Handler, Job("aspire-greeting", MaxAttempts = 3), UsesJobContext<RequestContextExtractor>]
public sealed partial class AspireGreetingJob(
	ILogger<AspireGreetingJob> logger,
	CurrentRequestContext currentRequestContext
)
{
	public sealed record Payload(string Name);

	private async ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		logger.LogInformation(
			"Preparing a greeting for {Name}; request IP {ClientIpAddress}, User-Agent {UserAgent}",
			payload.Name,
			currentRequestContext.Value?.ClientIpAddress,
			currentRequestContext.Value?.UserAgent
		);
		await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
		logger.LogInformation("Hello, {Name}!", payload.Name);
	}
}

[Handler, Job("aspire-heartbeat", Cron = "0 * * * * *")]
public sealed partial class AspireHeartbeatJob(
	ILogger<AspireHeartbeatJob> logger,
	TimeProvider timeProvider
)
{
	private ValueTask HandleAsync(NoPayload payload, CancellationToken cancellationToken)
	{
		logger.LogInformation(
			"Recurring Aspire heartbeat fired at {FiredAt}",
			timeProvider.GetUtcNow()
		);
		return ValueTask.CompletedTask;
	}
}

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
{
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);
		modelBuilder.AddImmediateJobs();
	}
}

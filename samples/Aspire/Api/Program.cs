using Immediate.Handlers.Shared;
using Immediate.Jobs.Aspire.Api;
using Immediate.Jobs.Aspire.Api.Context;
using Immediate.Jobs.Aspire.Api.Data;
using Immediate.Jobs.Aspire.Api.Endpoints;
using Immediate.Jobs.Aspire.Api.Telemetry;
using Immediate.Jobs.Aspire.Api.Workflows;
using Immediate.Jobs.Dashboard;
using Immediate.Jobs.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

// Without this, the assembly name would generate `AddImmediateJobsAspireApiJobs`.
[assembly: ImmediateAssemblyIdentifier("AspireApi")]

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentRequestContext>();
builder.Services.AddScoped<GameReleaseWorkflow>();
builder.Services.AddScoped<OrderFulfillmentWorkflow>();

var connectionString = builder.Configuration.GetConnectionString("jobs")
	?? throw new InvalidOperationException("The Aspire 'jobs' connection string is required.");
var aspireDashboardUrl = builder.Configuration["Telemetry:AspireDashboardUrl"] is { } configuredDashboardUrl
	? new Uri(configuredDashboardUrl, UriKind.Absolute)
	: null;

builder.Services.AddDbContextFactory<JobsDbContext>(options =>
	options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));

builder.Services.AddAspireApiHandlers();
builder.Services.AddImmediateJobsDashboard(options =>
{
	if (aspireDashboardUrl is not null)
		_ = options.AddAspireTelemetryLinks(aspireDashboardUrl);
});
builder.Services.AddAspireApiJobs(options =>
{
	_ = options.UseEntityFrameworkCore<JobsDbContext>();
	_ = options.UseSingleServer(); // Explicit; EF storage selects single-server mode implicitly when omitted.
	_ = options.UseFairQueues();
	options.MaxParallelJobs = 4;
	options.PollingInterval = TimeSpan.FromSeconds(5);
}).AddHealthCheck();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
	var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JobsDbContext>>();
	await using var dbContext = await dbContextFactory.CreateDbContextAsync();
	_ = await dbContext.Database.EnsureCreatedAsync();
}

_ = app.MapDefaultEndpoints();
_ = app.MapImmediateJobsDashboard("/jobs");

if (app.Environment.IsDevelopment())
{
	_ = app.MapSwagger("/openapi/{documentName}.json");
	_ = app.MapScalarApiReference(options => options
		.WithTitle("Immediate.Jobs Aspire sample")
		.DisableAgent());
}

app.MapGet("/", () => Results.Redirect("/scalar"))
	.ExcludeFromDescription();

_ = app.MapSampleApiEndpoints();

await app.RunAsync();

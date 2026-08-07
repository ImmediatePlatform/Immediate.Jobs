using Immediate.Jobs.Dashboard;
using Immediate.Jobs.DistributedAspire.Api.Telemetry;
using Immediate.Jobs.DistributedAspire.Shared;
using Immediate.Jobs.DistributedAspire.Shared.Data;
using Immediate.Jobs.DistributedAspire.Shared.Workflows;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<DistributedBatchWorkflow>();

var connectionString = builder.Configuration.GetConnectionString("jobs")
	?? throw new InvalidOperationException("The Aspire 'jobs' connection string is required.");
var aspireDashboardUrl = builder.Configuration["Telemetry:AspireDashboardUrl"] is { } configuredDashboardUrl
	? new Uri(configuredDashboardUrl, UriKind.Absolute)
	: null;

builder.Services.AddDbContextFactory<JobsDbContext>(options =>
	options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure().MigrationsAssembly("Immediate.Jobs.DistributedAspire.Shared")));

builder.Services.AddDistributedAspireHandlers();
builder.Services.AddImmediateJobsDashboard(options =>
{
	if (aspireDashboardUrl is not null)
		_ = options.AddAspireTelemetryLinks(aspireDashboardUrl);
});
builder.Services.AddDistributedAspireJobs()
	.UseFairQueues()
	.ConfigureStorage(o => o.UseSingleServer().UseDistributed())
	.Configure(o => o.PollingInterval = TimeSpan.FromSeconds(5))
	.AddHealthCheck();

foreach (var descriptor in builder.Services.Where(p => p.ServiceType == typeof(IHostedService)).ToArray())
{
	_ = builder.Services.Remove(descriptor);
}

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
	var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JobsDbContext>>();
	await using var dbContext = await dbContextFactory.CreateDbContextAsync();
	await dbContext.Database.MigrateAsync();
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

await app.RunAsync();

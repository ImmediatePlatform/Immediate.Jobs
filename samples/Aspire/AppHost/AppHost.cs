var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
	.WithDataVolume();
var jobsDatabase = postgres.AddDatabase("jobs");

var jobsApi = builder.AddProject<Projects.Immediate_Jobs_Aspire_Api>("jobs-api")
	.WithReference(jobsDatabase)
	.WaitFor(jobsDatabase)
	.WithExternalHttpEndpoints()
	.WithHttpHealthCheck("/health");

if (builder.Configuration["ASPNETCORE_URLS"]?
	.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
	.FirstOrDefault() is { } aspireDashboardUrl)
{
	_ = jobsApi.WithEnvironment("Telemetry__AspireDashboardUrl", aspireDashboardUrl);
}

builder.Build().Run();

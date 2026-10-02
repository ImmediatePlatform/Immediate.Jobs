var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
	.WithDataVolume();
var jobsDatabase = postgres.AddDatabase("jobs");

var jobsApi = builder.AddProject<Projects.Immediate_Jobs_Aspire_Api>("jobs-api")
	.WithReference(jobsDatabase)
	.WaitFor(jobsDatabase)
	.WithExternalHttpEndpoints()
	.WithUrlForEndpoint("http", static url => url.DisplayText = "Scalar")
	.WithUrlForEndpoint("http", static _ => new()
	{
		Url = "/health",
		DisplayText = "🏥 Health",
	})
	.WithUrlForEndpoint("http", static _ => new()
	{
		Url = "/jobs",
		DisplayText = "💼 Jobs",
	});

if (builder.Configuration["ASPNETCORE_URLS"]?
	.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
	.FirstOrDefault() is { } aspireDashboardUrl)
{
	_ = jobsApi.WithEnvironment("Telemetry__AspireDashboardUrl", aspireDashboardUrl);
}

await builder.Build().RunAsync();

var builder = DistributedApplication.CreateBuilder(args);

var username = builder.AddParameter("postgres-username", secret: true);
var password = builder.AddParameter("postgres-password", secret: true);

var postgres = builder
	.AddPostgres("postgres", username, password)
	.WithHostPort(5432)
	.WithDataVolume(isReadOnly: false)
	.WithLifetime(ContainerLifetime.Persistent);

var jobsDatabase = postgres.AddDatabase("jobs");

var pgAdmin = postgres
	.WithPgAdmin(pgAdmin => pgAdmin.WithHostPort(5050).WithLifetime(ContainerLifetime.Persistent));

var pgWeb = postgres
	.WithPgWeb(pgWeb => pgWeb.WithHostPort(5051).WithLifetime(ContainerLifetime.Persistent));

var jobsApi = builder.AddProject<Projects.Immediate_Jobs_DistributedAspire_Api>("jobs-api")
	.WithReference(jobsDatabase)
	.WaitFor(jobsDatabase)
	.WithExternalHttpEndpoints()
	.WithHttpHealthCheck("/health")
	.WithUrlForEndpoint("http", static url => url.DisplayText = "Scalar")
	.WithUrlForEndpoint("http", static _ => new()
	{
		Url = "/health",
		DisplayText = "🏥 Health"
	})
	.WithUrlForEndpoint("http", static _ => new()
	{
		Url = "/jobs",
		DisplayText = "💼 Jobs"
	});

if (builder.Configuration["ASPNETCORE_URLS"]?
	.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
	.FirstOrDefault() is { } aspireDashboardUrl)
{
	_ = jobsApi.WithEnvironment("Telemetry__AspireDashboardUrl", aspireDashboardUrl);
}

_ = builder.AddProject<Projects.Immediate_Jobs_DistributedAspire_Cli>("jobs-cli")
	.WithReference(jobsDatabase)
	.WaitFor(jobsDatabase)
	.WithExplicitStart();

builder.AddProject<Projects.Immediate_Jobs_DistributedAspire_Worker>("worker")
	.WithReference(jobsDatabase)
	.WaitFor(jobsDatabase)
	.WaitFor(jobsApi);

await builder.Build().RunAsync();

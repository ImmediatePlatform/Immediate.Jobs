var builder = DistributedApplication.CreateBuilder(args);

var username = builder.AddParameter("postgres-username", "admin");
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

var jobsApi = builder.AddDotnetProject("jobs-api", "../Api/Immediate.Jobs.DistributedAspire.Api.csproj")
	.WithReference(jobsDatabase)
	.WaitFor(jobsDatabase)
	.WithExternalHttpEndpoints()
	.WithHttpHealthCheck("/health")
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

_ = builder.AddDotnetProject("jobs-cli", "../Cli/Immediate.Jobs.DistributedAspire.Cli.csproj")
	.WithReference(jobsDatabase)
	.WaitFor(jobsDatabase)
	.WithExplicitStart();

builder.AddDotnetProject("worker", "../Worker/Immediate.Jobs.DistributedAspire.Worker.csproj")
	.WithReference(jobsDatabase)
	.WaitFor(jobsDatabase)
	.WaitFor(jobsApi)
	.WithReplicas(1);

await builder.Build().RunAsync();

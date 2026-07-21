var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
	.WithDataVolume();
var jobsDatabase = postgres.AddDatabase("jobs");

_ = builder.AddProject<Projects.Immediate_Jobs_Aspire_Api>("jobs-api")
	.WithReference(jobsDatabase)
	.WaitFor(jobsDatabase)
	.WithExternalHttpEndpoints()
	.WithHttpHealthCheck("/health");

builder.Build().Run();

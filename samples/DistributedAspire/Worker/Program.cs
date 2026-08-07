using Immediate.Jobs.DistributedAspire.Shared;
using Immediate.Jobs.DistributedAspire.Shared.Data;
using Immediate.Jobs.DistributedAspire.Shared.Workflows;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("jobs") ?? throw new InvalidOperationException("The Aspire 'jobs' connection string is required.");

builder.Services
			.AddScoped<DistributedBatchWorkflow>()
			.AddDistributedAspireHandlers()
			.AddDbContextFactory<JobsDbContext>(options => options.UseNpgsql(connectionString))
			.AddDistributedAspireJobs()
			.UseFairQueues()
			.ConfigureStorage(o => o.UseSingleServer().UseDistributed())
			.Configure(o => o.PollingInterval = TimeSpan.FromSeconds(5));

builder.AddServiceDefaults();

var host = builder.Build();
host.Run();

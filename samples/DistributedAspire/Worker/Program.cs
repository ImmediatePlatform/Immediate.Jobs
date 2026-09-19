using Immediate.Jobs.DistributedAspire.Shared;
using Immediate.Jobs.DistributedAspire.Shared.Data;
using Immediate.Jobs.DistributedAspire.Shared.Workflows;
using Immediate.Jobs.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("jobs") ?? throw new InvalidOperationException("The Aspire 'jobs' connection string is required.");

builder.Services
			.AddScoped<DistributedBatchWorkflow>()
			.AddDistributedAspireHandlers()
			.AddDbContextFactory<JobsDbContext>(options => options.UseNpgsql(connectionString))
			.AddDistributedAspireJobs()
			.UseFairQueues()
			.ConfigureStorage(o => o
				.UseEntityFrameworkCore<JobsDbContext>()
				.UseDistributed()
			)
			.ConfigureWorkers(o => o.PollingInterval = TimeSpan.FromSeconds(5));

builder.AddServiceDefaults();

var host = builder.Build();
host.Run();

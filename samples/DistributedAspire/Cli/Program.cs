using Immediate.Jobs.DistributedAspire.Shared;
using Immediate.Jobs.DistributedAspire.Shared.Data;
using Immediate.Jobs.DistributedAspire.Shared.Workflows;
using Immediate.Jobs.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateDefaultBuilder(args)
	.ConfigureServices((hostContext, services) =>
	{
		var connectionString = hostContext.Configuration.GetConnectionString("jobs") ?? throw new InvalidOperationException("The Aspire 'jobs' connection string is required.");

		_ = services
			.AddScoped<DistributedBatchWorkflow>()
			.AddDistributedAspireHandlers()
			.AddDbContextFactory<JobsDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()))
			.AddDistributedAspireJobs()
			.UseFairQueues()
			.ConfigureStorage(o => o.UseEntityFrameworkCore<JobsDbContext>().UseDistributed())
			.Configure(o => o.PollingInterval = TimeSpan.FromSeconds(5));

		foreach (var descriptor in services.Where(p => p.ServiceType == typeof(IHostedService)).ToArray())
		{
			_ = services.Remove(descriptor);
		}
	});

using var app = builder.Build();

await using var scope = app.Services.CreateAsyncScope();
var workflow = scope.ServiceProvider.GetRequiredService<DistributedBatchWorkflow>();
var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
await workflow.StartAsync(10, 50, CancellationToken.None);

logger.LogInformation("Done!");

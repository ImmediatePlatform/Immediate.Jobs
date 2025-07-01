using Immediate.Jobs.Contracts;
using Microsoft.Extensions.Hosting;

namespace Immediate.Jobs;

public sealed class JobManager(IStorageProvider storageProvider) : IHostedService
{
	Task IHostedService.StartAsync(CancellationToken cancellationToken)
	{
		throw new NotImplementedException();
	}

	Task IHostedService.StopAsync(CancellationToken cancellationToken)
	{
		throw new NotImplementedException();
	}
}

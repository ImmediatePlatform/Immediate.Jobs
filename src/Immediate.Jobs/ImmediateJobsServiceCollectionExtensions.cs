using Immediate.Jobs.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs;

public static class ImmediateJobsServiceCollectionExtensions
{
	public static IServiceCollection AddImmediateJobsManager(this IServiceCollection services)
	{
		return services
			.AddHostedService<JobManager>()
			.AddSingleton<JobManager>()
			.AddSingleton<IStorageProvider>(_ => new InMemoryStorageProvider());
	}
}

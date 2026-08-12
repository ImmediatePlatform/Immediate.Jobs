using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Validations.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Runtime registration methods used by generated application extensions.
/// </summary>
public static class ImmediateJobsRuntimeServiceCollectionExtensions
{
	/// <summary>
	/// 	Adds the scheduler runtime. Application code normally calls generated AddImmediateJobs instead.
	/// </summary>
	/// <param name="services">
	/// 	The service collection to which the runtime is added.
	/// </param>
	/// <returns>
	/// 	A builder for selecting storage and adding runtime integrations.
	/// </returns>
	public static ImmediateJobsBuilder AddImmediateJobsCore(
		this IServiceCollection services
	)
	{
		ArgumentNullException.ThrowIfNull(services);

		var optionsBuilder = services
			.AddOptionsWithValidateOnStart<ImmediateJobsOptions>()
			.Validate(
				o =>
				{
					ValidationException.ThrowIfInvalid(o, $@"Validation error for ""{nameof(ImmediateJobsOptions)}""");
					return true;
				}
			);

		var fairQueueOptionsBuilder = services
			.AddOptionsWithValidateOnStart<FairQueueOptions>()
			.Validate(
				o =>
				{
					ValidationException.ThrowIfInvalid(o, $@"Validation error for ""{nameof(FairQueueOptions)}""");
					return true;
				}
			);

		services.TryAddSingleton(TimeProvider.System);
		services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
		services.TryAddSingleton<IJobSerializer, SystemTextJsonJobSerializer>();
		services.TryAddSingleton<IJobStorage, InMemoryJobStorage>();

		services.TryAddScoped<BatchScheduler>();
		services.TryAddScoped<IBatchScheduler>(sp => sp.GetRequiredService<BatchScheduler>());

		services.TryAddScoped<JobMonitor>();
		services.TryAddScoped<IBatchMonitor>(static sp => sp.GetRequiredService<JobMonitor>());
		services.TryAddScoped<IJobMonitor>(static sp => sp.GetRequiredService<JobMonitor>());

		services.AddSingleton(JobQueueDefinition.Default);
		services.TryAddSingleton<JobSchedulerState>();
		services.TryAddSingleton<JobSchedulingService>();

		services.TryAddEnumerable(
			ServiceDescriptor.Singleton<IHostedService, JobSchedulingService>(
				ServiceProviderServiceExtensions.GetRequiredService<JobSchedulingService>
			)
		);

		return new(services, optionsBuilder, fairQueueOptionsBuilder);
	}
}

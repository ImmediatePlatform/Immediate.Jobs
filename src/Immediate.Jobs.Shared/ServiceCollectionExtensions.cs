using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Immediate.Jobs.Shared;

/// <summary>Runtime registration methods used by generated application extensions.</summary>
public static class ImmediateJobsRuntimeServiceCollectionExtensions
{
	/// <summary>Adds the scheduler runtime. Application code normally calls generated AddImmediateJobs instead.</summary>
	/// <param name="services">The service collection to which the runtime is added.</param>
	/// <param name="configure">An optional callback that configures the runtime.</param>
	/// <returns>A builder for selecting storage and adding runtime integrations.</returns>
	public static ImmediateJobsBuilder AddImmediateJobsCore(
		this IServiceCollection services,
		Action<ImmediateJobsOptions>? configure = null
	)
	{
		ArgumentNullException.ThrowIfNull(services);

		var options = new ImmediateJobsOptions();
		configure?.Invoke(options);
		if (options.StorageFactory is null)
		{
			if (options.StorageModeExplicitlySelected)
				throw new ImmediateJobException("Select a durable storage provider before choosing single-server or distributed mode.");

			_ = options.UseInMemory();
		}

		options.Validate();

		services.TryAddSingleton(options);
		services.TryAddSingleton(TimeProvider.System);
		services.TryAddSingleton<IIdGenerator>(GuidIdGenerator.Instance);
		services.TryAddSingleton<IJobSerializer, SystemTextJsonJobSerializer>();
		services.TryAddSingleton(options.CreateStorage);
		services.TryAddSingleton(static sp =>
			sp.GetRequiredService<IJobStorage>() as IRecurringJobStorage
				?? null!);
		services.TryAddSingleton(static sp =>
			sp.GetRequiredService<IJobStorage>() as IJobGraphStorage
				?? null!);
		services.TryAddScoped<JobBatchScheduler>();
		services.TryAddScoped<IJobBatchScheduler>(static sp =>
			sp.GetService<IJobGraphStorage>() is null
				? null!
				: sp.GetRequiredService<JobBatchScheduler>());
		services.TryAddScoped<JobMonitor>();
		services.TryAddScoped<IJobBatchMonitor>(static sp =>
			sp.GetService<IJobGraphStorage>() is null
				? null!
				: sp.GetRequiredService<JobMonitor>());
		services.TryAddScoped<IJobMonitor>(static sp =>
			sp.GetRequiredService<JobMonitor>());
		_ = services.AddSingleton(JobQueueDefinition.Default);
		services.TryAddSingleton<JobSchedulerState>();
		services.TryAddSingleton<JobSchedulerService>();
		services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobSchedulerHostedService>());
		return new(services);
	}

	/// <summary>Replaces the default GUID job and batch identifier generator.</summary>
	/// <typeparam name="TGenerator">The identifier generator implementation.</typeparam>
	/// <param name="builder">The Immediate.Jobs builder.</param>
	/// <returns>The supplied builder.</returns>
	public static ImmediateJobsBuilder UseIdGenerator<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TGenerator
	>(
		this ImmediateJobsBuilder builder
	)
		where TGenerator : class, IIdGenerator
	{
		ArgumentNullException.ThrowIfNull(builder);
		_ = builder.Services.Replace(ServiceDescriptor.Singleton<IIdGenerator, TGenerator>());
		return builder;
	}

	/// <summary>Adds scheduler liveness and storage connectivity to the health-check system.</summary>
	/// <param name="builder">The Immediate.Jobs builder.</param>
	/// <param name="name">The registered health-check name.</param>
	/// <param name="failureStatus">The status reported when the check fails.</param>
	/// <param name="tags">The tags associated with the health check.</param>
	/// <returns>The supplied builder.</returns>
	public static ImmediateJobsBuilder AddHealthCheck(
		this ImmediateJobsBuilder builder,
		string name = "immediate-jobs",
		HealthStatus? failureStatus = null,
		IEnumerable<string>? tags = null
	)
	{
		ArgumentNullException.ThrowIfNull(builder);
		_ = builder.Services.AddHealthChecks().AddCheck<ImmediateJobsHealthCheck>(name, failureStatus, tags ?? []);
		return builder;
	}
}

[SuppressMessage("Performance", "CA1812", Justification = "Activated by dependency injection.")]
internal sealed class JobSchedulerHostedService(JobSchedulerService scheduler) : IHostedService
{
	public Task StartAsync(CancellationToken cancellationToken) => scheduler.StartAsync(cancellationToken);

	public Task StopAsync(CancellationToken cancellationToken) => scheduler.StopAsync(cancellationToken);
}

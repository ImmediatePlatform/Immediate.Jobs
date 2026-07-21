using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Runtime registration methods used by generated application extensions.</summary>
public static class ImmediateJobsRuntimeServiceCollectionExtensions
{
	/// <summary>Adds the scheduler runtime. Application code normally calls generated AddImmediateJobs instead.</summary>
	public static Immediate.Jobs.ImmediateJobsBuilder AddImmediateJobsCore(
		this IServiceCollection services,
		Action<Immediate.Jobs.ImmediateJobsOptions>? configure = null
	)
	{
		ArgumentNullException.ThrowIfNull(services);

		var options = new Immediate.Jobs.ImmediateJobsOptions();
		configure?.Invoke(options);
		if (options.StorageFactory is null)
		{
			if (options.StorageModeExplicitlySelected)
				throw new InvalidOperationException("Select a durable storage provider before choosing single-server or distributed mode.");
			options.UseInMemory();
		}
		options.Validate();

		services.TryAddSingleton(options);
		services.TryAddSingleton(TimeProvider.System);
		services.TryAddSingleton<Immediate.Jobs.IJobSerializer, Immediate.Jobs.SystemTextJsonJobSerializer>();
		services.TryAddSingleton<Immediate.Jobs.IJobStorage>(sp => options.CreateStorage(sp));
		services.AddSingleton(Immediate.Jobs.JobQueueDefinition.Default);
		services.TryAddSingleton<Immediate.Jobs.JobSchedulerState>();
		services.TryAddSingleton<Immediate.Jobs.JobSchedulerService>();
		services.AddSingleton<IHostedService>(
			static sp => sp.GetRequiredService<Immediate.Jobs.JobSchedulerService>()
		);
		return new(services);
	}

	/// <summary>Adds scheduler liveness and storage connectivity to the health-check system.</summary>
	public static Immediate.Jobs.ImmediateJobsBuilder AddHealthCheck(
		this Immediate.Jobs.ImmediateJobsBuilder builder,
		string name = "immediate-jobs",
		HealthStatus? failureStatus = null,
		IEnumerable<string>? tags = null
	)
	{
		ArgumentNullException.ThrowIfNull(builder);
		builder.Services.AddHealthChecks().AddCheck<Immediate.Jobs.ImmediateJobsHealthCheck>(name, failureStatus, tags ?? []);
		return builder;
	}
}

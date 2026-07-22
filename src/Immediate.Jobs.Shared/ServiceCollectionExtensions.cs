using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

#pragma warning disable IDE0130 // Extension methods intentionally follow the namespace of the extended type.
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

/// <summary>Runtime registration methods used by generated application extensions.</summary>
public static class ImmediateJobsRuntimeServiceCollectionExtensions
{
	/// <summary>Adds the scheduler runtime. Application code normally calls generated AddImmediateJobs instead.</summary>
	public static Immediate.Jobs.Shared.ImmediateJobsBuilder AddImmediateJobsCore(
		this IServiceCollection services,
		Action<Immediate.Jobs.Shared.ImmediateJobsOptions>? configure = null
	)
	{
		ArgumentNullException.ThrowIfNull(services);

		var options = new Immediate.Jobs.Shared.ImmediateJobsOptions();
		configure?.Invoke(options);
		if (options.StorageFactory is null)
		{
			if (options.StorageModeExplicitlySelected)
				throw new Immediate.Jobs.Shared.ImmediateJobException("Select a durable storage provider before choosing single-server or distributed mode.");

			_ = options.UseInMemory();
		}

		options.Validate();

		services.TryAddSingleton(options);
		services.TryAddSingleton(TimeProvider.System);
		services.TryAddSingleton<Immediate.Jobs.Shared.IIdGenerator>(Immediate.Jobs.Shared.GuidIdGenerator.Instance);
		services.TryAddSingleton<Immediate.Jobs.Shared.IJobSerializer, Immediate.Jobs.Shared.SystemTextJsonJobSerializer>();
		services.TryAddSingleton<Immediate.Jobs.Shared.IJobStorage>(sp => options.CreateStorage(sp));
		services.TryAddScoped<Immediate.Jobs.Shared.IJobBatchScheduler, Immediate.Jobs.Shared.JobBatchScheduler>();
		services.TryAddScoped<Immediate.Jobs.Shared.JobMonitor>();
		services.TryAddScoped<Immediate.Jobs.Shared.IJobBatchMonitor>(static sp =>
			sp.GetRequiredService<Immediate.Jobs.Shared.JobMonitor>());
		services.TryAddScoped<Immediate.Jobs.Shared.IJobMonitor>(static sp =>
			sp.GetRequiredService<Immediate.Jobs.Shared.JobMonitor>());
		_ = services.AddSingleton(Immediate.Jobs.Shared.JobQueueDefinition.Default);
		services.TryAddSingleton<Immediate.Jobs.Shared.JobSchedulerState>();
		services.TryAddSingleton<Immediate.Jobs.Shared.JobSchedulerService>();
		_ = services.AddSingleton<IHostedService>(
			static sp => sp.GetRequiredService<Immediate.Jobs.Shared.JobSchedulerService>()
		);
		return new(services);
	}

	/// <summary>Replaces the default GUID job and batch identifier generator.</summary>
	public static Immediate.Jobs.Shared.ImmediateJobsBuilder UseIdGenerator<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TGenerator
	>(
		this Immediate.Jobs.Shared.ImmediateJobsBuilder builder
	)
		where TGenerator : class, Immediate.Jobs.Shared.IIdGenerator
	{
		ArgumentNullException.ThrowIfNull(builder);
		_ = builder.Services.Replace(ServiceDescriptor.Singleton<Immediate.Jobs.Shared.IIdGenerator, TGenerator>());
		return builder;
	}

	/// <summary>Adds scheduler liveness and storage connectivity to the health-check system.</summary>
	public static Immediate.Jobs.Shared.ImmediateJobsBuilder AddHealthCheck(
		this Immediate.Jobs.Shared.ImmediateJobsBuilder builder,
		string name = "immediate-jobs",
		HealthStatus? failureStatus = null,
		IEnumerable<string>? tags = null
	)
	{
		ArgumentNullException.ThrowIfNull(builder);
		_ = builder.Services.AddHealthChecks().AddCheck<Immediate.Jobs.Shared.ImmediateJobsHealthCheck>(name, failureStatus, tags ?? []);
		return builder;
	}
}

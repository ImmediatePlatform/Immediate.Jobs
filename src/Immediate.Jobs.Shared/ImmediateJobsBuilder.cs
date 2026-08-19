using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	The fluent registration result returned by generated AddImmediateJobs methods.
/// </summary>
public interface IImmediateJobsBuilder
{
	/// <summary>
	/// 	The service collection being configured.
	/// </summary>
	IServiceCollection Services { get; }

	/// <summary>
	/// 	Adds scheduler liveness and storage connectivity to the health-check system.
	/// </summary>
	/// <param name="name">
	/// 	The registered health-check name.
	/// </param>
	/// <param name="failureStatus">
	/// 	The status reported when the check fails.
	/// </param>
	/// <param name="tags">
	/// 	The tags associated with the health check.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsBuilder AddHealthCheck(string name = "immediate-jobs", HealthStatus? failureStatus = null, IEnumerable<string>? tags = null);

	/// <summary>
	///		Provides an extension point to configure the options using a user provided configuration method.
	/// </summary>
	/// <param name="configureJobs">
	///		The configuration method used to set the options.
	///	</param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsBuilder ConfigureWorkers(Action<OptionsBuilder<ImmediateJobsOptions>> configureJobs);

	/// <summary>
	///		Provides an extension point to configure the options using a user provided configuration method.
	/// </summary>
	/// <param name="configureJobs">
	///		The configuration method used to set the options.
	///	</param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsBuilder ConfigureWorkers(Action<ImmediateJobsOptions> configureJobs);

	/// <summary>
	///		Disables workers from running in this application.
	/// </summary>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsBuilder DisableWorkers();

	/// <summary>
	///		Enables Fair Queues.
	/// </summary>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsBuilder UseFairQueues();

	/// <summary>
	///		Enables Fair Queues.
	/// </summary>
	/// <param name="configureFairQueues">
	///		A configuration method used to configure the fair options policy.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsBuilder UseFairQueues(Action<OptionsBuilder<FairQueueOptions>> configureFairQueues);

	/// <summary>
	/// 	Replaces the default GUID job and batch identifier generator.
	/// </summary>
	/// <typeparam name="TGenerator">
	/// 	The identifier generator implementation.
	/// </typeparam>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsBuilder UseIdGenerator<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TGenerator
	>() where TGenerator : class, IIdGenerator;

	/// <summary>
	///		Configures the job storage used by <c>Immediate.Jobs</c>.
	/// </summary>
	/// <param name="configure">
	///		The callback used to configure the job storage.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsBuilder ConfigureStorage(Action<IImmediateJobsStorageBuilder> configure);
}

internal sealed class ImmediateJobsBuilder : IImmediateJobsBuilder
{
	internal ImmediateJobsBuilder(
		IServiceCollection services,
		OptionsBuilder<ImmediateJobsOptions> optionsBuilder,
		OptionsBuilder<ImmediateJobsStorageOptions> storageOptionsBuilder,
		OptionsBuilder<FairQueueOptions> fairQueueOptionsBuilder
	)
	{
		Services = services;
		OptionsBuilder = optionsBuilder;
		StorageOptionsBuilder = storageOptionsBuilder;
		FairQueueOptionsBuilder = fairQueueOptionsBuilder;
	}

	public IServiceCollection Services { get; }

	internal OptionsBuilder<ImmediateJobsOptions> OptionsBuilder { get; }
	internal OptionsBuilder<ImmediateJobsStorageOptions> StorageOptionsBuilder { get; }
	internal OptionsBuilder<FairQueueOptions> FairQueueOptionsBuilder { get; }

	public IImmediateJobsBuilder UseIdGenerator<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TGenerator
	>() where TGenerator : class, IIdGenerator
	{
		Services.Replace(ServiceDescriptor.Singleton<IIdGenerator, TGenerator>());
		return this;
	}

	public IImmediateJobsBuilder AddHealthCheck(
		string name = "immediate-jobs",
		HealthStatus? failureStatus = null,
		IEnumerable<string>? tags = null
	)
	{
		Services.AddHealthChecks().AddCheck<ImmediateJobsHealthCheck>(name, failureStatus, tags ?? []);
		return this;
	}

	public IImmediateJobsBuilder DisableWorkers()
	{
		OptionsBuilder.PostConfigure(o => o.IsJobSchedulingServiceEnabled = false);
		return this;
	}

	public IImmediateJobsBuilder ConfigureWorkers(
		Action<OptionsBuilder<ImmediateJobsOptions>> configureJobs
	)
	{
		ArgumentNullException.ThrowIfNull(configureJobs);

		configureJobs(OptionsBuilder);
		return this;
	}

	public IImmediateJobsBuilder ConfigureWorkers(
		Action<ImmediateJobsOptions> configureJobs
	)
	{
		ArgumentNullException.ThrowIfNull(configureJobs);

		OptionsBuilder.Configure(configureJobs);
		return this;
	}

	public IImmediateJobsBuilder UseFairQueues()
	{
		FairQueueOptionsBuilder.Configure(o => o.Enabled = true);
		return this;
	}

	public IImmediateJobsBuilder UseFairQueues(
		Action<OptionsBuilder<FairQueueOptions>> configureFairQueues
	)
	{
		ArgumentNullException.ThrowIfNull(configureFairQueues);

		FairQueueOptionsBuilder.Configure(o => o.Enabled = true);
		configureFairQueues(FairQueueOptionsBuilder);
		return this;
	}

	public IImmediateJobsBuilder ConfigureStorage(
		Action<IImmediateJobsStorageBuilder> configure
	)
	{
		ArgumentNullException.ThrowIfNull(configure);

		if (Services.Any(s => s.ServiceType == typeof(ImmediateJobsStorageBuilder)))
			ImmediateJobException.Throw("Cannot configure storage multiple times.");

		StorageOptionsBuilder.Configure(o => o.Configured = true);

		var builder = new ImmediateJobsStorageBuilder(Services);
		configure(builder);
		builder.ValidateAndRegister();

		Services.AddSingleton(builder);

		return this;
	}
}

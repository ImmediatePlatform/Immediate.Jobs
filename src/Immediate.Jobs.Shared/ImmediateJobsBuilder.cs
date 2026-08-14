using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	The fluent registration result returned by generated AddImmediateJobs methods.
/// </summary>
public sealed class ImmediateJobsBuilder
{
	internal ImmediateJobsBuilder(
		IServiceCollection services,
		OptionsBuilder<ImmediateJobsOptions> optionsBuilder,
		OptionsBuilder<FairQueueOptions> fairQueueOptionsBuilder
	)
	{
		Services = services;
		OptionsBuilder = optionsBuilder;
		FairQueueOptionsBuilder = fairQueueOptionsBuilder;
	}

	internal IServiceCollection Services { get; }
	internal OptionsBuilder<ImmediateJobsOptions> OptionsBuilder { get; }
	internal OptionsBuilder<FairQueueOptions> FairQueueOptionsBuilder { get; }

	/// <summary>
	/// 	Replaces the default GUID job and batch identifier generator.
	/// </summary>
	/// <typeparam name="TGenerator">
	/// 	The identifier generator implementation.
	/// </typeparam>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	public ImmediateJobsBuilder UseIdGenerator<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TGenerator
	>()
		where TGenerator : class, IIdGenerator
	{
		Services.Replace(ServiceDescriptor.Singleton<IIdGenerator, TGenerator>());
		return this;
	}

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
	public ImmediateJobsBuilder AddHealthCheck(
		string name = "immediate-jobs",
		HealthStatus? failureStatus = null,
		IEnumerable<string>? tags = null
	)
	{
		Services.AddHealthChecks().AddCheck<ImmediateJobsHealthCheck>(name, failureStatus, tags ?? []);
		return this;
	}

	/// <summary>
	///		Registers the dependency injection container to bind <see cref="ImmediateJobsOptions"/> against
	///		the <see cref="IConfiguration"/> obtained from the DI service provider.
	/// </summary>
	/// <param name="configurationSectionPath">
	///		The name of the configuration section to bind from.
	///	</param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	public ImmediateJobsBuilder Configure(
		string configurationSectionPath
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configurationSectionPath);

		OptionsBuilder.BindConfiguration(configurationSectionPath);
		return this;
	}

	/// <summary>
	///		Registers a configuration instance which <see cref="ImmediateJobsOptions"/> will bind against.
	/// </summary>
	/// <param name="configurationSection">
	///		The configuration being bound.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	public ImmediateJobsBuilder Configure(
		IConfiguration configurationSection
	)
	{
		ArgumentNullException.ThrowIfNull(configurationSection);

		OptionsBuilder.Bind(configurationSection);
		return this;
	}

	/// <summary>
	///		Registers an action used to configure an <see cref="ImmediateJobsOptions"/>.
	/// </summary>
	/// <param name="configureOptions">
	///		The action used to configure the options.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	public ImmediateJobsBuilder Configure(
		Action<ImmediateJobsOptions> configureOptions
	)
	{
		ArgumentNullException.ThrowIfNull(configureOptions);

		OptionsBuilder.Configure(configureOptions);
		return this;
	}

	/// <summary>
	///		Enables Fair Queues.
	/// </summary>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	public ImmediateJobsBuilder UseFairQueues()
	{
		FairQueueOptionsBuilder.PostConfigure(o => o.Enabled = true);
		return this;
	}

	/// <summary>
	///		Enables Fair Queues and registers the dependency injection container to bind <see cref="FairQueueOptions"/> against
	///		the <see cref="IConfiguration"/> obtained from the DI service provider.
	/// </summary>
	/// <param name="configurationSectionPath">
	///		The name of the configuration section to bind from.
	///	</param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	public ImmediateJobsBuilder UseFairQueues(
		string configurationSectionPath
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configurationSectionPath);

		FairQueueOptionsBuilder
			.BindConfiguration(configurationSectionPath)
			.PostConfigure(o => o.Enabled = true);

		return this;
	}

	/// <summary>
	///		Enables Fair Queues and registers a configuration instance which <see cref="FairQueueOptions"/> will bind against.
	/// </summary>
	/// <param name="configurationSection">
	///		The configuration being bound.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	public ImmediateJobsBuilder UseFairQueues(
		IConfiguration configurationSection
	)
	{
		ArgumentNullException.ThrowIfNull(configurationSection);

		FairQueueOptionsBuilder
			.Bind(configurationSection)
			.PostConfigure(o => o.Enabled = true);

		return this;
	}

	/// <summary>
	///		Enables Fair Queues and registers an action used to configure an <see cref="FairQueueOptions"/>.
	/// </summary>
	/// <param name="configureOptions">
	///		The action used to configure the options.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	public ImmediateJobsBuilder UseFairQueues(
		Action<FairQueueOptions> configureOptions
	)
	{
		ArgumentNullException.ThrowIfNull(configureOptions);

		FairQueueOptionsBuilder
			.Configure(configureOptions)
			.PostConfigure(o => o.Enabled = true);

		return this;
	}

	/// <summary>
	///		Configures the job storage used by <c>Immediate.Jobs</c>.
	/// </summary>
	/// <param name="configure">
	///		The callback used to configure the job storage.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	public ImmediateJobsBuilder ConfigureStorage(
		Action<ImmediateJobsStorageBuilder> configure
	)
	{
		ArgumentNullException.ThrowIfNull(configure);

		if (Services.Any(s => s.ServiceType == typeof(ImmediateJobsStorageBuilder)))
			ImmediateJobException.Throw("Cannot configure storage multiple times.");

		var builder = new ImmediateJobsStorageBuilder(Services);
		Services.AddSingleton(builder);
		configure(builder);

		builder.ValidateAndRegister(Services);

		return this;
	}
}

/// <summary>
/// 	The fluent registration object used to configure job storage.
/// </summary>
public sealed class ImmediateJobsStorageBuilder
{
	private enum JobStorageMode
	{
		None,
		InMemory,
		SingleServer,
		Distributed,
	}

	private JobStorageMode _storageMode;
	private Func<IServiceProvider, IJobStorage>? _factory;

	internal ImmediateJobsStorageBuilder(IServiceCollection services)
	{
		Services = services;
	}

	/// <summary>
	/// 	The service collection being configured.
	/// </summary>
	public IServiceCollection Services { get; }

	/// <summary>
	/// 	Selects the non-durable, single-node in-memory provider.
	/// </summary>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsStorageBuilder UseInMemory()
	{
		if (_storageMode is not (JobStorageMode.None or JobStorageMode.InMemory))
			ImmediateJobException.Throw("Cannot select in-memory job storage when other job storage options have been selected.");

		_storageMode = JobStorageMode.InMemory;
		return this;
	}

	/// <summary>
	///		Selects a durable storage provider. By default, it is used as a write-through replica of the
	///		authoritative in-process store for a single scheduler server.
	/// </summary>
	/// <param name="factory">
	/// 	The factory that creates the durable storage provider.
	/// </param>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsStorageBuilder UseStorage(Func<IServiceProvider, IJobStorage> factory)
	{
		ArgumentNullException.ThrowIfNull(factory);

		if (_storageMode is JobStorageMode.InMemory)
			ImmediateJobException.Throw("Cannot provide a durable storage provider when in-memory job storage has already been selected.");

		if (_factory is { })
			ImmediateJobException.Throw("A durable storage provider has already been provided.");

		_factory = factory;
		return this;
	}

	/// <summary>
	/// 	Selects memory-primary, durable-replica operation for one scheduler server.
	/// </summary>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsStorageBuilder UseSingleServer()
	{
		if (_storageMode is not (JobStorageMode.None or JobStorageMode.SingleServer))
			ImmediateJobException.Throw("Cannot select single-server operation mode when other job storage options have been selected.");

		if (_factory is null)
			ImmediateJobException.Throw("Cannot select single-server operation mode when no durable storage provider has been provided.");

		_storageMode = JobStorageMode.SingleServer;
		return this;
	}

	/// <summary>
	/// 	Selects memory-primary operation with the supplied durable replica.
	/// </summary>
	/// <param name="durableStorageFactory">
	/// 	The factory that creates the durable storage replica.
	/// </param>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsStorageBuilder UseSingleServer(Func<IServiceProvider, IJobStorage> durableStorageFactory)
	{
		UseStorage(durableStorageFactory);
		UseSingleServer();
		return this;
	}

	/// <summary>
	/// 	Selects durable-storage-primary operation for multiple scheduler servers.
	/// </summary>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsStorageBuilder UseDistributed()
	{
		if (_storageMode is not (JobStorageMode.None or JobStorageMode.Distributed))
			ImmediateJobException.Throw("Cannot select distributed operation mode when other job storage options have been selected.");

		if (_factory is null)
			ImmediateJobException.Throw("Cannot select distributed operation mode when no durable storage provider has been provided.");

		_storageMode = JobStorageMode.Distributed;
		return this;
	}

	/// <summary>
	/// 	Selects durable-storage-primary operation for multiple scheduler servers.
	/// </summary>
	/// <param name="durableStorageFactory">
	/// 	The factory that creates the durable storage replica.
	/// </param>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsStorageBuilder UseDistributed(Func<IServiceProvider, IJobStorage> durableStorageFactory)
	{
		UseStorage(durableStorageFactory);
		UseDistributed();
		return this;
	}

	internal void ValidateAndRegister(IServiceCollection services)
	{
		// explicit in-memory
		if (_storageMode is JobStorageMode.InMemory)
		{
			// error should be thrown earlier, but just in case...
			if (_factory is { })
				ImmediateJobException.Throw("Cannot provide a durable storage provider when in-memory job storage has already been selected.");

			return;
		}

		if (_factory is null)
		{
			// none, with no factory is base-state; aka in-memory
			if (_storageMode is JobStorageMode.None)
				return;

			ImmediateJobException.Throw("Durable storage is required, but no durable storage provider has been provided.");
		}

		// only check for explicit distributed
		if (_storageMode is JobStorageMode.Distributed)
		{
			services.Replace(ServiceDescriptor.Singleton(_factory));
			return;
		}

		// none or explicit single-server are both single-server
		services.Replace(
			ServiceDescriptor.Singleton<IJobStorage, SingleServerJobStorage>(
				sp => new SingleServerJobStorage(
					_factory(sp),
					sp.GetRequiredService<TimeProvider>()
				)
			)
		);
	}
}

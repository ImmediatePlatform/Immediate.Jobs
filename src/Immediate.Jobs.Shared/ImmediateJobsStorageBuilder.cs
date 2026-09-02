using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	The fluent registration object used to configure job storage.
/// </summary>
public interface IImmediateJobsStorageBuilder
{
	/// <summary>
	/// 	The service collection being configured.
	/// </summary>
	IServiceCollection Services { get; }

	/// <summary>
	/// 	Selects the non-durable, single-node in-memory provider.
	/// </summary>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsStorageBuilder UseInMemory();

	/// <summary>
	///		Selects a durable storage provider. By default, it is used as a write-through replica of the
	///		authoritative in-process store for a single scheduler server.
	/// </summary>
	/// <param name="factory">
	/// 	The factory that creates the durable storage provider.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsStorageBuilder UseStorage(Func<IServiceProvider, IJobStorage> factory);

	/// <summary>
	///		Selects a durable storage provider. By default, it is used as a write-through replica of the
	///		authoritative in-process store for a single scheduler server.
	/// </summary>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsStorageBuilder UseStorage<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJobStorage
	>() where TJobStorage : class, IJobStorage;

	/// <summary>
	/// 	Selects memory-primary, durable-replica operation for one scheduler server.
	/// </summary>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsStorageBuilder UseSingleServer();

	/// <summary>
	/// 	Selects memory-primary operation with the supplied durable replica.
	/// </summary>
	/// <param name="durableStorageFactory">
	/// 	The factory that creates the durable storage replica.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsStorageBuilder UseSingleServer(Func<IServiceProvider, IJobStorage> durableStorageFactory);

	/// <summary>
	/// 	Selects durable-storage-primary operation for multiple scheduler servers.
	/// </summary>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsStorageBuilder UseDistributed();

	/// <summary>
	/// 	Selects durable-storage-primary operation for multiple scheduler servers.
	/// </summary>
	/// <param name="durableStorageFactory">
	/// 	The factory that creates the durable storage replica.
	/// </param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsStorageBuilder UseDistributed(Func<IServiceProvider, IJobStorage> durableStorageFactory);
}

internal sealed class ImmediateJobsStorageBuilder : IImmediateJobsStorageBuilder
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

	public IServiceCollection Services { get; }

	public IImmediateJobsStorageBuilder UseInMemory()
	{
		if (_storageMode is not (JobStorageMode.None or JobStorageMode.InMemory))
			ImmediateJobException.Throw("Cannot select in-memory job storage when other job storage options have been selected.");

		_storageMode = JobStorageMode.InMemory;
		return this;
	}

	public IImmediateJobsStorageBuilder UseStorage(Func<IServiceProvider, IJobStorage> factory)
	{
		ArgumentNullException.ThrowIfNull(factory);

		if (_storageMode is JobStorageMode.InMemory)
			ImmediateJobException.Throw("Cannot provide a durable storage provider when in-memory job storage has already been selected.");

		if (_factory is { })
			ImmediateJobException.Throw("A durable storage provider has already been provided.");

		_factory = factory;
		return this;
	}

	public IImmediateJobsStorageBuilder UseStorage<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJobStorage
	>() where TJobStorage : class, IJobStorage
	{
		if (_storageMode is JobStorageMode.InMemory)
			ImmediateJobException.Throw("Cannot provide a durable storage provider when in-memory job storage has already been selected.");

		if (_factory is { })
			ImmediateJobException.Throw("A durable storage provider has already been provided.");

		Services.AddSingleton<TJobStorage>();
		_factory = sp => sp.GetRequiredService<TJobStorage>();
		return this;
	}

	public IImmediateJobsStorageBuilder UseSingleServer()
	{
		if (_storageMode is not (JobStorageMode.None or JobStorageMode.SingleServer))
			ImmediateJobException.Throw("Cannot select single-server operation mode when other job storage options have been selected.");

		_storageMode = JobStorageMode.SingleServer;
		return this;
	}

	public IImmediateJobsStorageBuilder UseSingleServer(Func<IServiceProvider, IJobStorage> durableStorageFactory)
	{
		UseStorage(durableStorageFactory);
		UseSingleServer();
		return this;
	}

	public IImmediateJobsStorageBuilder UseDistributed()
	{
		if (_storageMode is not (JobStorageMode.None or JobStorageMode.Distributed))
			ImmediateJobException.Throw("Cannot select distributed operation mode when other job storage options have been selected.");

		_storageMode = JobStorageMode.Distributed;
		return this;
	}

	public IImmediateJobsStorageBuilder UseDistributed(Func<IServiceProvider, IJobStorage> durableStorageFactory)
	{
		UseStorage(durableStorageFactory);
		UseDistributed();
		return this;
	}

	internal void ValidateAndRegister()
	{
		switch (_storageMode)
		{
			case JobStorageMode.InMemory:
				if (_factory is { })
					throw new ImmediateJobException("Cannot provide a durable storage provider when in-memory job storage has already been selected.");
				break;

			case JobStorageMode.Distributed:
				if (_factory is null)
					throw new ImmediateJobException("Durable storage is required, but no durable storage provider has been provided.");

				Services.Replace(ServiceDescriptor.Singleton(_factory));
				break;

			case JobStorageMode.None:
			case JobStorageMode.SingleServer:
			default:
				if (_factory is null)
					throw new ImmediateJobException("Durable storage is required, but no durable storage provider has been provided.");

				// none or explicit single-server are both single-server
				Services.Replace(
					ServiceDescriptor.Singleton<IJobStorage, SingleServerJobStorage>(
						sp => new SingleServerJobStorage(
							_factory(sp),
							sp.GetRequiredService<TimeProvider>(),
							sp.GetRequiredService<ILogger<SingleServerJobStorage>>()
						)
					)
				);
				break;
		}
	}
}

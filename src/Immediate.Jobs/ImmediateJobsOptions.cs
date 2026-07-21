using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs;

/// <summary>Determines whether workers coordinate through memory or durable storage.</summary>
public enum JobStorageMode
{
	/// <summary>Uses only the in-process store. Work is lost when the process exits.</summary>
	InMemory,

	/// <summary>Uses in-process storage as the authority and writes every change to durable storage for restart recovery.</summary>
	SingleServer,

	/// <summary>Uses durable storage as the authority so multiple scheduler nodes can coordinate.</summary>
	Distributed,
}

/// <summary>Global scheduler and worker options.</summary>
public sealed class ImmediateJobsOptions
{
	private bool _storageModeExplicitlySelected;

	/// <summary>Maximum concurrently executing jobs on this node.</summary>
	public int MaxParallelJobs { get; set; } = Environment.ProcessorCount;

	/// <summary>Maximum number claimed in one storage round-trip.</summary>
	public int AcquisitionBatchSize { get; set; } = 32;

	/// <summary>Fallback interval between storage polls.</summary>
	public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

	/// <summary>Duration of an acquired job lease.</summary>
	public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>Maximum time allowed for workers to drain during shutdown.</summary>
	public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>Retention for successful history.</summary>
	public TimeSpan SucceededRetention { get; set; } = TimeSpan.FromHours(24);

	/// <summary>Retention for failed history.</summary>
	public TimeSpan FailedRetention { get; set; } = TimeSpan.FromDays(7);

	/// <summary>How frequently terminal history is purged.</summary>
	public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

	/// <summary>The provider factory selected by a Use* extension.</summary>
	public Func<IServiceProvider, IJobStorage>? StorageFactory { get; private set; }

	/// <summary>The topology used by the scheduler.</summary>
	public JobStorageMode StorageMode { get; private set; } = JobStorageMode.SingleServer;

	internal bool StorageModeExplicitlySelected => _storageModeExplicitlySelected;

	/// <summary>Selects the non-durable, single-node in-memory provider.</summary>
	public ImmediateJobsOptions UseInMemory()
	{
		StorageFactory = static services => new InMemoryJobStorage(services.GetRequiredService<TimeProvider>());
		StorageMode = JobStorageMode.InMemory;
		_storageModeExplicitlySelected = false;
		return this;
	}

	/// <summary>
	/// Selects a durable storage provider. By default, it is used as a write-through replica of the
	/// authoritative in-process store for a single scheduler server.
	/// </summary>
	public ImmediateJobsOptions UseStorage(Func<IServiceProvider, IJobStorage> factory)
	{
		ArgumentNullException.ThrowIfNull(factory);
		StorageFactory = factory;
		if (!_storageModeExplicitlySelected)
			StorageMode = JobStorageMode.SingleServer;
		return this;
	}

	/// <summary>Selects memory-primary, durable-replica operation for one scheduler server.</summary>
	public ImmediateJobsOptions UseSingleServer()
	{
		StorageMode = JobStorageMode.SingleServer;
		_storageModeExplicitlySelected = true;
		return this;
	}

	/// <summary>Selects memory-primary operation with the supplied durable replica.</summary>
	public ImmediateJobsOptions UseSingleServer(Func<IServiceProvider, IJobStorage> durableStorageFactory)
	{
		UseStorage(durableStorageFactory);
		return UseSingleServer();
	}

	/// <summary>Selects durable-storage-primary operation for multiple scheduler servers.</summary>
	public ImmediateJobsOptions UseDistributed()
	{
		StorageMode = JobStorageMode.Distributed;
		_storageModeExplicitlySelected = true;
		return this;
	}

	internal IJobStorage CreateStorage(IServiceProvider services)
	{
		var storage = StorageFactory!(services)
			?? throw new InvalidOperationException("The Immediate.Jobs storage factory returned null.");
		return StorageMode == JobStorageMode.SingleServer
			? new SingleServerJobStorage(storage, services.GetRequiredService<TimeProvider>())
			: storage;
	}

	internal void Validate()
	{
		if (MaxParallelJobs <= 0)
			throw new InvalidOperationException("MaxParallelJobs must be greater than zero.");
		if (AcquisitionBatchSize <= 0)
			throw new InvalidOperationException("AcquisitionBatchSize must be greater than zero.");
		if (PollingInterval <= TimeSpan.Zero)
			throw new InvalidOperationException("PollingInterval must be greater than zero.");
		if (LeaseDuration <= TimeSpan.Zero)
			throw new InvalidOperationException("LeaseDuration must be greater than zero.");
		if (ShutdownTimeout < TimeSpan.Zero)
			throw new InvalidOperationException("ShutdownTimeout cannot be negative.");
	}
}

/// <summary>The fluent registration result returned by generated AddImmediateJobs methods.</summary>
public sealed class ImmediateJobsBuilder
{
	internal ImmediateJobsBuilder(IServiceCollection services)
	{
		Services = services;
	}

	/// <summary>The application service collection.</summary>
	public IServiceCollection Services { get; }
}

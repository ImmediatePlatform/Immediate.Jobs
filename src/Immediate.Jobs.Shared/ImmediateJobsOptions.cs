using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Determines whether workers coordinate through memory or durable storage.
/// </summary>
public enum JobStorageMode
{
	/// <summary>
	/// 	Uses only the in-process store. Work is lost when the process exits.
	/// </summary>
	InMemory,

	/// <summary>
	/// 	Uses in-process storage as the authority and writes every change to durable storage for restart recovery.
	/// </summary>
	SingleServer,

	/// <summary>
	/// 	Uses durable storage as the authority so multiple scheduler nodes can coordinate.
	/// </summary>
	Distributed,
}

/// <summary>
/// 	Configures noisy-neighbor detection and group scheduling for fair queues.
/// </summary>
public sealed class FairQueueOptions
{
	/// <summary>
	/// In-flight share above which a group may be considered noisy. The value must be greater
	/// than zero and less than or equal to one.
	/// 
	/// </summary>
	/// <value>
	/// 	The in-flight share threshold used to identify noisy groups.
	/// </value>
	public double ConcurrencyShareThreshold { get; set; } = 0.10;

	/// <summary>
	/// 	Minimum number of a group's in-flight jobs before it may be considered noisy.
	/// </summary>
	/// <value>
	/// 	The minimum in-flight job count required for noisy-group detection.
	/// </value>
	public int MinInflightForNoisy { get; set; } = 30;

	/// <summary>
	/// 	Whether due work is interleaved across groups independently of noisy-neighbor detection.
	/// </summary>
	/// <value><see langword="true"/> to interleave due work across groups; otherwise, <see langword="false"/>.
	/// </value>
	public bool GroupRoundRobin { get; set; } = true;

	internal void Validate()
	{
		if (double.IsNaN(ConcurrencyShareThreshold)
			|| ConcurrencyShareThreshold <= 0
			|| ConcurrencyShareThreshold > 1)
		{
			throw new ImmediateJobException("Fair queue ConcurrencyShareThreshold must be greater than zero and less than or equal to one.");
		}

		if (MinInflightForNoisy <= 0)
			throw new ImmediateJobException("Fair queue MinInflightForNoisy must be greater than zero.");
	}

	internal FairQueuePolicy ToPolicy() => new()
	{
		ConcurrencyShareThreshold = ConcurrencyShareThreshold,
		MinInflightForNoisy = MinInflightForNoisy,
		GroupRoundRobin = GroupRoundRobin,
	};
}

/// <summary>
/// 	Global scheduler and worker options.
/// </summary>
public sealed class ImmediateJobsOptions
{
	/// <summary>
	/// 	Maximum concurrently executing jobs on this node.
	/// </summary>
	/// <remarks>
	/// The default exceeds the core count because jobs are typically IO-bound. The practical ceiling
	/// is usually the database connection pool: each executing job holds a service scope, and most
	/// jobs hold one pooled connection for their duration.
	/// 
	/// </remarks>
	/// <value>
	/// 	The maximum number of jobs that may execute concurrently on this node.
	/// </value>
	public int MaxParallelJobs { get; set; } = Math.Clamp(Environment.ProcessorCount * 4, 8, 32);

	/// <summary>
	/// 	Maximum number claimed in one storage round-trip.
	/// </summary>
	/// <value>
	/// 	The maximum number of jobs claimed in one acquisition.
	/// </value>
	public int AcquisitionBatchSize { get; set; } = 32;

	/// <summary>
	/// 	Fallback interval between storage polls.
	/// </summary>
	/// <value>
	/// 	The interval between storage polls.
	/// </value>
	public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

	/// <summary>
	/// 	Duration of an acquired job lease.
	/// </summary>
	/// <value>
	/// 	The duration assigned to an acquired job lease.
	/// </value>
	public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// 	Maximum time allowed for workers to drain during shutdown.
	/// </summary>
	/// <value>
	/// 	The maximum worker-drain duration during shutdown.
	/// </value>
	public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// 	Retention for successful history.
	/// </summary>
	/// <value>
	/// 	The duration for which successful job history is retained.
	/// </value>
	public TimeSpan SucceededRetention { get; set; } = TimeSpan.FromHours(24);

	/// <summary>
	/// 	Retention for failed history.
	/// </summary>
	/// <value>
	/// 	The duration for which failed job history is retained.
	/// </value>
	public TimeSpan FailedRetention { get; set; } = TimeSpan.FromDays(7);

	/// <summary>
	/// 	Retention for successful batches and all of their members and edges.
	/// </summary>
	/// <value>
	/// 	The duration for which successful batch history is retained.
	/// </value>
	public TimeSpan BatchSucceededRetention { get; set; } = TimeSpan.FromHours(24);

	/// <summary>
	/// 	Retention for failed or cancelled batches and all of their members and edges.
	/// </summary>
	/// <value>
	/// 	The duration for which failed or cancelled batch history is retained.
	/// </value>
	public TimeSpan BatchFailedRetention { get; set; } = TimeSpan.FromDays(7);

	/// <summary>
	/// 	How frequently terminal history is purged.
	/// </summary>
	/// <value>
	/// 	The interval between terminal-history purge operations.
	/// </value>
	public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

	/// <summary>
	/// 	The provider factory selected by a Use* extension.
	/// </summary>
	/// <value>
	/// 	The configured storage-provider factory, or <see langword="null"/> when none has been selected.
	/// </value>
	public Func<IServiceProvider, IJobStorage>? StorageFactory { get; private set; }

	/// <summary>
	/// 	The topology used by the scheduler.
	/// </summary>
	/// <value>
	/// 	The configured storage topology.
	/// </value>
	public JobStorageMode StorageMode { get; private set; } = JobStorageMode.SingleServer;

	/// <summary>
	/// 	Fair queue settings, or <see langword="null"/> when fair acquisition is disabled.
	/// </summary>
	/// <value>
	/// 	The fair queue settings, or <see langword="null"/> when fair acquisition is disabled.
	/// </value>
	public FairQueueOptions? FairQueues { get; private set; }

	internal bool StorageModeExplicitlySelected { get; private set; }

	/// <summary>
	/// 	Enables fair acquisition for jobs carrying a runtime group id.
	/// </summary>
	/// <param name="configure">
	/// 	An optional delegate that configures fair queue behavior.
	/// </param>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsOptions UseFairQueues(Action<FairQueueOptions>? configure = null)
	{
		var options = new FairQueueOptions();
		configure?.Invoke(options);
		FairQueues = options;
		return this;
	}

	/// <summary>
	/// 	Selects the non-durable, single-node in-memory provider.
	/// </summary>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsOptions UseInMemory()
	{
		StorageFactory = static services => new InMemoryJobStorage(services.GetRequiredService<TimeProvider>());
		StorageMode = JobStorageMode.InMemory;
		StorageModeExplicitlySelected = false;
		return this;
	}

	/// <summary>
	/// Selects a durable storage provider. By default, it is used as a write-through replica of the
	/// authoritative in-process store for a single scheduler server.
	/// 
	/// </summary>
	/// <param name="factory">
	/// 	The factory that creates the durable storage provider.
	/// </param>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsOptions UseStorage(Func<IServiceProvider, IJobStorage> factory)
	{
		ArgumentNullException.ThrowIfNull(factory);
		StorageFactory = factory;
		if (!StorageModeExplicitlySelected)
			StorageMode = JobStorageMode.SingleServer;
		return this;
	}

	/// <summary>
	/// 	Selects memory-primary, durable-replica operation for one scheduler server.
	/// </summary>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsOptions UseSingleServer()
	{
		StorageMode = JobStorageMode.SingleServer;
		StorageModeExplicitlySelected = true;
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
	public ImmediateJobsOptions UseSingleServer(Func<IServiceProvider, IJobStorage> durableStorageFactory)
	{
		_ = UseStorage(durableStorageFactory);
		return UseSingleServer();
	}

	/// <summary>
	/// 	Selects durable-storage-primary operation for multiple scheduler servers.
	/// </summary>
	/// <returns>
	/// 	This options instance.
	/// </returns>
	public ImmediateJobsOptions UseDistributed()
	{
		StorageMode = JobStorageMode.Distributed;
		StorageModeExplicitlySelected = true;
		return this;
	}

	internal IJobStorage CreateStorage(IServiceProvider services)
	{
		var storage = StorageFactory!(services)
			?? throw new ImmediateJobException("The Immediate.Jobs storage factory returned null.");
		if (StorageMode != JobStorageMode.SingleServer)
			return storage;

		try
		{
			return new SingleServerJobStorage(storage, services.GetRequiredService<TimeProvider>());
		}
		catch
		{
			TryDisposeStorage(storage);
			throw;
		}
	}

	private static void TryDisposeStorage(IJobStorage storage)
	{
		try
		{
			storage.DisposeAsync().AsTask().GetAwaiter().GetResult();
		}
#pragma warning disable CA1031 // Cleanup must not replace the storage-construction exception.
		catch (Exception)
#pragma warning restore CA1031
		{
		}
	}

	internal void Validate()
	{
		if (MaxParallelJobs <= 0)
			throw new ImmediateJobException("MaxParallelJobs must be greater than zero.");
		if (AcquisitionBatchSize <= 0)
			throw new ImmediateJobException("AcquisitionBatchSize must be greater than zero.");
		if (PollingInterval <= TimeSpan.Zero)
			throw new ImmediateJobException("PollingInterval must be greater than zero.");
		if (LeaseDuration <= TimeSpan.Zero)
			throw new ImmediateJobException("LeaseDuration must be greater than zero.");
		if (ShutdownTimeout < TimeSpan.Zero)
			throw new ImmediateJobException("ShutdownTimeout cannot be negative.");
		if (SucceededRetention < TimeSpan.Zero || FailedRetention < TimeSpan.Zero ||
			BatchSucceededRetention < TimeSpan.Zero || BatchFailedRetention < TimeSpan.Zero)
		{
			throw new ImmediateJobException("Retention periods cannot be negative.");
		}

		FairQueues?.Validate();
	}
}

/// <summary>
/// 	The fluent registration result returned by generated AddImmediateJobs methods.
/// </summary>
public sealed class ImmediateJobsBuilder
{
	internal ImmediateJobsBuilder(IServiceCollection services)
	{
		Services = services;
	}

	/// <summary>
	/// 	The application service collection.
	/// </summary>
	/// <value>
	/// 	The service collection being configured.
	/// </value>
	public IServiceCollection Services { get; }
}

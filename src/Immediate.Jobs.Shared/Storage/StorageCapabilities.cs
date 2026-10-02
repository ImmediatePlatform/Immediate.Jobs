namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// 	Optional feature sets implemented by the active storage provider.
/// </summary>
[Flags]
public enum StorageCapabilities
{
	/// <summary>
	/// 	No storage capabilities are available.
	/// </summary>
	None = 0,

	/// <summary>
	/// 	Queueing, execution, history, monitoring, recurring schedules, and fair acquisition.
	/// </summary>
	Queue = 1,

	/// <summary>
	/// 	Atomic batches, dependency graphs, and continuations.
	/// </summary>
	Graph = 4,
}

/// <summary>
/// 	Detects optional capabilities from the interfaces implemented by a storage provider.
/// </summary>
public static class JobStorageCapabilities
{
	/// <summary>
	/// 	Returns the capabilities implemented by <paramref name="storage"/>.
	/// </summary>
	/// <param name="storage">
	/// 	The storage provider to inspect.
	/// </param>
	/// <returns>
	/// 	The capabilities implemented by the storage provider.
	/// </returns>
	public static StorageCapabilities GetCapabilities(this IJobStorage storage)
	{
		ArgumentNullException.ThrowIfNull(storage);

		var capabilities = StorageCapabilities.Queue;
		if (storage is IJobGraphStorage)
			capabilities |= StorageCapabilities.Graph;
		return capabilities;
	}
}

internal static class JobStorageCapabilityGuards
{
	internal const string GraphNotSupportedMessage =
		"Batches & continuations require a graph-capable storage provider (a SQL database). " +
		"The configured provider implements the queue capability only.";

	internal const string SingleServerReplicaNotSupportedMessage =
		"This storage provider cannot be used as a single-server durable store.";

	internal static IJobGraphStorage RequireGraph(IJobStorage storage) =>
		storage as IJobGraphStorage ?? throw new NotSupportedException(GraphNotSupportedMessage);
}

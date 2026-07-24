namespace Immediate.Jobs.Shared;

/// <summary>Optional feature sets implemented by the active storage provider.</summary>
[Flags]
public enum StorageCapabilities
{
	/// <summary>No storage capabilities are available.</summary>
	None = 0,

	/// <summary>Ordinary queueing, execution, history, and monitoring.</summary>
	Queue = 1,

	/// <summary>Durable recurring schedules and occurrence materialization.</summary>
	Recurring = 2,

	/// <summary>Atomic batches, dependency graphs, and continuations.</summary>
	Graph = 4,
}

/// <summary>Detects optional capabilities from the interfaces implemented by a storage provider.</summary>
public static class JobStorageCapabilities
{
	/// <summary>Returns the capabilities implemented by <paramref name="storage"/>.</summary>
	public static StorageCapabilities GetCapabilities(this IJobStorage storage)
	{
		ArgumentNullException.ThrowIfNull(storage);

		var capabilities = StorageCapabilities.Queue;
		if (storage is IRecurringJobStorage)
			capabilities |= StorageCapabilities.Recurring;
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

	internal const string RecurringNotSupportedMessage =
		"Recurring schedules require a recurring-capable storage provider. " +
		"The configured provider implements the queue capability only.";

	internal static IJobGraphStorage RequireGraph(IJobStorage storage) =>
		storage as IJobGraphStorage ?? throw new NotSupportedException(GraphNotSupportedMessage);

	internal static IRecurringJobStorage RequireRecurring(IJobStorage storage) =>
		storage as IRecurringJobStorage ?? throw new NotSupportedException(RecurringNotSupportedMessage);
}

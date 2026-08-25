using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Testing.Storage;

/// <summary>
/// 	Provides the framework-neutral executable contract for job storage providers.
/// </summary>
public static class JobStorageConformanceSuite
{
	private const StorageCapabilities KnownCapabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Recurring |
		StorageCapabilities.Graph |
		StorageCapabilities.FairQueues |
		StorageCapabilities.Replica;

	/// <summary>
	/// 	Gets the queue cases and the optional suites selected by <paramref name="capabilities"/>.
	/// </summary>
	/// <param name="capabilities">
	/// 	The complete capability set the provider test fixture expects its registered storage to implement.
	/// </param>
	/// <returns>Individually discoverable conformance test cases.</returns>
	public static IReadOnlyList<JobStorageConformanceTestCase> GetCases(StorageCapabilities capabilities)
	{
		if ((capabilities & ~KnownCapabilities) != StorageCapabilities.None)
			throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities, "The capability set contains unknown flags.");

		if (!capabilities.HasFlag(StorageCapabilities.Queue))
			throw new ArgumentException("Every IJobStorage provider must advertise the Queue capability.", nameof(capabilities));

		return QueueStorageConformance.Cases
			.Concat(AddOptionalCases(capabilities, StorageCapabilities.Recurring, RecurringStorageConformance.Cases))
			.Concat(AddOptionalCases(capabilities, StorageCapabilities.Graph, GraphStorageConformance.Cases))
			.Concat(AddOptionalCases(capabilities, StorageCapabilities.FairQueues, FairQueueStorageConformance.Cases))
			.Concat(AddOptionalCases(capabilities, StorageCapabilities.Replica, ReplicaStorageConformance.Cases))
			.Select(definition => new JobStorageConformanceTestCase(definition, capabilities))
			.ToList();
	}

	/// <summary>
	///		A map of all known cases by their case name.
	/// </summary>
	public static IReadOnlyDictionary<string, JobStorageConformanceTestCase> AllCasesByName { get; } =
		GetCases(KnownCapabilities)
			.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

	private static IEnumerable<JobStorageConformanceCaseDefinition> AddOptionalCases(
		StorageCapabilities advertisedCapabilities,
		StorageCapabilities suiteCapability,
		IReadOnlyCollection<JobStorageConformanceCaseDefinition> cases
	)
	{
		return advertisedCapabilities.HasFlag(suiteCapability)
			? cases
			: [];
	}
}

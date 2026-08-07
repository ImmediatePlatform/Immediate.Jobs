using Immediate.Jobs.Shared.Storage;

#pragma warning disable IDE0130
namespace Immediate.Jobs.Testing;

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
		if ((capabilities & StorageCapabilities.Queue) == StorageCapabilities.None)
			throw new ArgumentException("Every IJobStorage provider must advertise the Queue capability.", nameof(capabilities));

		var definitions = new List<JobStorageConformanceCaseDefinition>(QueueStorageConformance.Cases.Count + 4);
		definitions.AddRange(QueueStorageConformance.Cases);

		AddOptionalCases(definitions, capabilities, StorageCapabilities.Recurring, RecurringStorageConformance.Cases);
		AddOptionalCases(definitions, capabilities, StorageCapabilities.Graph, GraphStorageConformance.Cases);
		AddOptionalCases(definitions, capabilities, StorageCapabilities.FairQueues, FairQueueStorageConformance.Cases);
		AddOptionalCases(definitions, capabilities, StorageCapabilities.Replica, ReplicaStorageConformance.Cases);

		return Array.AsReadOnly(
			definitions
				.Select(definition => new JobStorageConformanceTestCase(definition, capabilities))
				.ToArray()
		);
	}

	private static void AddOptionalCases(
		List<JobStorageConformanceCaseDefinition> destination,
		StorageCapabilities advertisedCapabilities,
		StorageCapabilities suiteCapability,
		IReadOnlyCollection<JobStorageConformanceCaseDefinition> cases
	)
	{
		if ((advertisedCapabilities & suiteCapability) == suiteCapability)
			destination.AddRange(cases);
	}
}

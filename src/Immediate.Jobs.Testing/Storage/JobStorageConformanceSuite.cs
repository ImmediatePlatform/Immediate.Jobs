using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Testing.Storage;

/// <summary>
/// 	Provides the framework-neutral executable contract for job storage providers.
/// </summary>
public static class JobStorageConformanceSuite
{
	private const StorageCapabilities KnownCapabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Graph;

	/// <summary>
	/// 	Gets the queue cases and the optional suites selected by <paramref name="capabilities"/>.
	/// </summary>
	/// <param name="capabilities">
	/// 	The complete capability set the provider test fixture expects its registered storage to implement.
	/// </param>
	/// <param name="includeSingleServerReplicaCases">
	/// 	Whether to include the cases for <see cref="IJobGraphStorage.AcquireJobsAsync"/>, which only providers that
	/// 	can act as a single-server durable store implement. Requires <see cref="StorageCapabilities.Graph"/>.
	/// </param>
	/// <returns>
	/// 	Individually discoverable conformance test cases.
	/// </returns>
	public static IReadOnlyList<JobStorageConformanceTestCase> GetCases(
		StorageCapabilities capabilities,
		bool includeSingleServerReplicaCases = false
	)
	{
		if ((capabilities & ~KnownCapabilities) != StorageCapabilities.None)
			throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities, "The capability set contains unknown flags.");

		if (!capabilities.HasFlag(StorageCapabilities.Queue))
			throw new ArgumentException("Every IJobStorage provider must advertise the Queue capability.", nameof(capabilities));

		if (includeSingleServerReplicaCases && !capabilities.HasFlag(StorageCapabilities.Graph))
			throw new ArgumentException("Single-server replica cases require the Graph capability.", nameof(includeSingleServerReplicaCases));

		return QueueStorageConformance.Cases
			.Concat(DefinitionAcquisitionStorageConformance.Cases)
			.Concat(AddOptionalCases(capabilities, StorageCapabilities.Queue, RecurringStorageConformance.Cases))
			.Concat(AddOptionalCases(capabilities, StorageCapabilities.Graph, GraphStorageConformance.Cases))
			.Concat(DefinitionCatalogStorageConformance.Cases)
			.Concat(TagStorageConformance.Cases)
			.Concat(FairQueueStorageConformance.Cases)
			.Concat(AddOptionalCases(capabilities, StorageCapabilities.Queue, TriggerStorageConformance.Cases))
			.Concat(includeSingleServerReplicaCases ? ReplicaStorageConformance.Cases : [])
			.ToList();
	}

	/// <summary>
	/// 	A map of all known cases by their case name.
	/// </summary>
	public static IReadOnlyDictionary<string, JobStorageConformanceTestCase> AllCasesByName { get; } =
		GetCases(KnownCapabilities, includeSingleServerReplicaCases: true)
			.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

	private static IEnumerable<JobStorageConformanceTestCase> AddOptionalCases(
		StorageCapabilities advertisedCapabilities,
		StorageCapabilities suiteCapability,
		IReadOnlyCollection<JobStorageConformanceTestCase> cases
	)
	{
		return advertisedCapabilities.HasFlag(suiteCapability)
			? cases.Where(c => advertisedCapabilities.HasFlag(c.RequiredCapabilities))
			: [];
	}
}

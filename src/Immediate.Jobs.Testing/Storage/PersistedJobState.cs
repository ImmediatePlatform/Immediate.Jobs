using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Testing.Storage;

/// <summary>
///	    Represents the set of data that should be pre-loaded to the database before the test starts.
/// </summary>
public sealed class PersistedJobState
{
	/// <summary>
	///		The set of jobs that should be pre-loaded.
	/// </summary>
	public required IReadOnlyList<JobRecord> Jobs { get; init; }

	/// <summary>
	///		The set of batches that should be pre-loaded.
	/// </summary>
	public required IReadOnlyList<BatchRecord> Batches { get; init; }

	/// <summary>
	///	    The set of edges connecting jobs and batches that should be pre-loaded.
	/// </summary>
	public required IReadOnlyList<JobContinuationEdge> Edges { get; init; }
}

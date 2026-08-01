using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
/// Provider capability required by single-server mode to mirror the exact set of jobs selected by its authoritative
/// in-process queue. Custom providers only need this capability when used as a single-server durable replica.
/// </summary>
public interface IJobStorageReplica
{
	/// <summary>
	/// 	Acquires the specified due invocations for the supplied worker.
	/// </summary>
	/// <param name="jobIds">
	/// 	The due invocation identifiers to acquire.
	/// </param>
	/// <param name="workerId">
	/// 	The identifier of the worker taking ownership.
	/// </param>
	/// <param name="lease">
	/// 	The lease duration assigned to acquired invocations.
	/// </param>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the storage operation.
	/// </param>
	/// <returns>
	/// 	The invocations acquired for the worker.
	/// </returns>
	ValueTask<IReadOnlyList<JobRecord>> AcquireJobsAsync(
		IReadOnlyCollection<string> jobIds,
		string workerId,
		TimeSpan lease,
		CancellationToken cancellationToken = default
	);
}

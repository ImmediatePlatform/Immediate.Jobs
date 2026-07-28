namespace Immediate.Jobs.Shared;

/// <summary>An opaque reference to a durable job invocation.</summary>
public readonly struct JobHandle : IEquatable<JobHandle>
{
	/// <summary>Creates a handle for an existing invocation identifier.</summary>
	/// <param name="id">The opaque invocation identifier.</param>
	public JobHandle(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		Id = id;
	}

	internal JobHandle(string id, JobBatch? batch)
		: this(id) => Batch = batch;

	/// <summary>The opaque invocation identifier.</summary>
	/// <value>The opaque invocation identifier.</value>
	public string Id { get; }

	internal JobBatch? Batch { get; }

	/// <inheritdoc />
	public bool Equals(JobHandle other) => string.Equals(Id, other.Id, StringComparison.Ordinal);

	/// <inheritdoc />
	public override bool Equals(object? obj) => obj is JobHandle other && Equals(other);

	/// <inheritdoc />
	public override int GetHashCode() => Id is null ? 0 : StringComparer.Ordinal.GetHashCode(Id);

	/// <summary>Compares two handles by their opaque invocation identifier.</summary>
	/// <param name="left">The first handle to compare.</param>
	/// <param name="right">The second handle to compare.</param>
	/// <returns><see langword="true"/> when the handles have the same invocation identifier; otherwise, <see langword="false"/>.</returns>
	public static bool operator ==(JobHandle left, JobHandle right) => left.Equals(right);

	/// <summary>Compares two handles by their opaque invocation identifier.</summary>
	/// <param name="left">The first handle to compare.</param>
	/// <param name="right">The second handle to compare.</param>
	/// <returns><see langword="true"/> when the handles have different invocation identifiers; otherwise, <see langword="false"/>.</returns>
	public static bool operator !=(JobHandle left, JobHandle right) => !left.Equals(right);

	/// <inheritdoc />
	public override string ToString() => Id ?? string.Empty;
}

/// <summary>An opaque reference to a committed atomic batch.</summary>
public sealed record BatchHandle
{
	/// <summary>Creates a handle for an existing batch identifier.</summary>
	/// <param name="id">The opaque batch identifier.</param>
	public BatchHandle(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		Id = id;
	}

	/// <summary>The opaque batch identifier.</summary>
	/// <value>The opaque batch identifier.</value>
	public string Id { get; }
}

/// <summary>Determines how a continuation evaluates its parents.</summary>
public enum ContinuationTrigger
{
	/// <summary>Run only when every parent succeeds; otherwise cancel the continuation.</summary>
	Success,

	/// <summary>Run only when every parent is terminal and at least one parent failed.</summary>
	Failure,

	/// <summary>Run after every parent reaches any terminal state.</summary>
	Complete,
}

/// <summary>Determines how work scheduled by a running job joins its workflow.</summary>
public enum ContinuationOptions
{
	/// <summary>Schedule outside the current batch and do not modify existing continuations.</summary>
	Detached,

	/// <summary>Add the job to the current batch as a parallel branch.</summary>
	BesideContinuations,

	/// <summary>Add the job to the current batch and make current waiters depend on it too.</summary>
	BeforeContinuations,
}

/// <summary>The durable lifecycle state of an atomic batch.</summary>
public enum BatchState
{
	/// <summary>At least one member has not reached a terminal state.</summary>
	Executing,
	/// <summary>Every member succeeded.</summary>
	Succeeded,
	/// <summary>At least one member failed.</summary>
	Failed,
	/// <summary>No member failed and at least one member was cancelled.</summary>
	Cancelled,
}

/// <summary>A durable atomic-batch header.</summary>
public sealed record JobBatchRecord
{
	/// <summary>The opaque batch identifier.</summary>
	/// <value>The opaque batch identifier.</value>
	public required string Id { get; init; }
	/// <summary>UTC time at which the batch was created.</summary>
	/// <value>The UTC batch-creation time.</value>
	public required DateTimeOffset CreatedAt { get; init; }
	/// <summary>Total members added to the batch.</summary>
	/// <value>The total number of members added to the batch.</value>
	public required int TotalJobs { get; init; }
	/// <summary>Members that have not reached a terminal state.</summary>
	/// <value>The number of non-terminal members.</value>
	public required int PendingCount { get; init; }
	/// <summary>Members that completed successfully.</summary>
	/// <value>The number of successful members.</value>
	public int SucceededCount { get; init; }
	/// <summary>Members that exhausted their attempts.</summary>
	/// <value>The number of failed members.</value>
	public int FailedCount { get; init; }
	/// <summary>Members cancelled by an explicit action or dependency violation.</summary>
	/// <value>The number of cancelled members.</value>
	public int CancelledCount { get; init; }
	/// <summary>UTC time at which the first member was acquired.</summary>
	/// <value>The UTC batch-start time, if any member has been acquired.</value>
	public DateTimeOffset? StartedAt { get; init; }
	/// <summary>UTC terminal completion time.</summary>
	/// <value>The UTC terminal completion time, if the batch is terminal.</value>
	public DateTimeOffset? CompletedAt { get; init; }
	/// <summary>The aggregate lifecycle state.</summary>
	/// <value>The aggregate batch state.</value>
	public required BatchState State { get; init; }
}

/// <summary>A durable dependency edge from a job or batch to a child job.</summary>
public sealed record JobContinuationEdge
{
	/// <summary>The waiting child invocation.</summary>
	/// <value>The waiting child invocation identifier.</value>
	public required string ChildJobId { get; init; }
	/// <summary>The parent invocation for a job-to-job dependency.</summary>
	/// <value>The parent invocation identifier, or <see langword="null"/> for a batch-to-job dependency.</value>
	public string? ParentJobId { get; init; }
	/// <summary>The parent batch for a batch-to-job dependency.</summary>
	/// <value>The parent batch identifier, or <see langword="null"/> for a job-to-job dependency.</value>
	public string? ParentBatchId { get; init; }
	/// <summary>The condition under which the edge is satisfied.</summary>
	/// <value>The dependency trigger.</value>
	public ContinuationTrigger Trigger { get; init; } = ContinuationTrigger.Success;
}

/// <summary>A continuation buffered during a running job attempt and committed only on success.</summary>
public sealed record JobContinuationAddition
{
	/// <summary>The fully serialized new invocation.</summary>
	/// <value>The new invocation to commit.</value>
	public required JobRecord Job { get; init; }
	/// <summary>How the new invocation joins and modifies the current workflow.</summary>
	/// <value>The workflow-joining behavior.</value>
	public required ContinuationOptions Options { get; init; }
	/// <summary>The dependency trigger from the running job to the new invocation.</summary>
	/// <value>The dependency trigger from the running invocation.</value>
	public ContinuationTrigger Trigger { get; init; } = ContinuationTrigger.Success;
}

/// <summary>Per-attempt buffer used by generated schedulers during job execution.</summary>
public sealed class JobExecutionBuffer
{
	private readonly Lock _gate = new();
	private readonly List<JobContinuationAddition> _additions = [];
	private bool _sealed;

	internal void Add(JobContinuationAddition addition)
	{
		lock (_gate)
		{
			if (_sealed)
				throw new ImmediateJobException("The execution buffer is sealed and cannot accept additional continuations.");
			_additions.Add(addition);
		}
	}

	internal IReadOnlyList<JobContinuationAddition> SealAndSnapshot()
	{
		lock (_gate)
		{
			if (_sealed)
				throw new ImmediateJobException("The execution buffer has already been sealed.");
			_sealed = true;
			return _additions.Count == 0
				? Array.Empty<JobContinuationAddition>()
				: Array.AsReadOnly(_additions.ToArray());
		}
	}
}

/// <summary>Aggregate progress for an atomic batch.</summary>
/// <param name="Id">The opaque batch identifier.</param>
/// <param name="State">The aggregate batch state.</param>
/// <param name="Total">The total number of batch members.</param>
/// <param name="Succeeded">The number of successful members.</param>
/// <param name="Failed">The number of failed members.</param>
/// <param name="Cancelled">The number of cancelled members.</param>
/// <param name="Remaining">The number of non-terminal members.</param>
/// <param name="CreatedAt">The UTC batch-creation time.</param>
/// <param name="StartedAt">The UTC time at which the first member was acquired, if any.</param>
/// <param name="CompletedAt">The UTC terminal completion time, if any.</param>
/// <param name="FractionSettled">The fraction of members that have reached a terminal state.</param>
public sealed record BatchStatus(
	string Id,
	BatchState State,
	int Total,
	int Succeeded,
	int Failed,
	int Cancelled,
	int Remaining,
	DateTimeOffset CreatedAt,
	DateTimeOffset? StartedAt,
	DateTimeOffset? CompletedAt,
	double FractionSettled
)
{
	/// <summary>Calculates settled progress, treating an empty batch as fully settled.</summary>
	/// <param name="total">The total number of batch members.</param>
	/// <param name="remaining">The number of non-terminal members.</param>
	/// <returns>The fraction of members that have reached a terminal state.</returns>
	public static double CalculateFractionSettled(int total, int remaining) =>
		total == 0 ? 1d : (double)(total - remaining) / total;
}

/// <summary>Filters members returned from a batch.</summary>
public sealed record BatchMemberQuery
{
	/// <summary>Optional lifecycle-state filter.</summary>
	/// <value>The lifecycle state to match, or <see langword="null"/> to match every state.</value>
	public JobState? State { get; init; }
	/// <summary>Number of members to skip.</summary>
	/// <value>The number of members to skip.</value>
	public int Skip { get; init; }
	/// <summary>Maximum members to return.</summary>
	/// <value>The maximum number of members to return.</value>
	public int Take { get; init; } = 100;
}

/// <summary>Filters batches for dashboard presentation.</summary>
public sealed record JobBatchQuery
{
	/// <summary>Optional aggregate-state filter.</summary>
	/// <value>The aggregate state to match, or <see langword="null"/> to match every state.</value>
	public BatchState? State { get; init; }
	/// <summary>Number of batches to skip.</summary>
	/// <value>The number of batches to skip.</value>
	public int Skip { get; init; }
	/// <summary>Maximum batches to return.</summary>
	/// <value>The maximum number of batches to return.</value>
	public int Take { get; init; } = 100;
}

/// <summary>Monitoring data for one batch member.</summary>
/// <param name="JobId">The invocation identifier.</param>
/// <param name="JobName">The stable job name.</param>
/// <param name="QueueName">The persisted queue name.</param>
/// <param name="State">The current invocation state.</param>
/// <param name="Attempt">The number of execution attempts already started.</param>
/// <param name="CreatedAt">The UTC invocation-creation time.</param>
/// <param name="CompletedAt">The UTC terminal completion time, if any.</param>
/// <param name="LastError">The latest failure details, if any.</param>
public sealed record BatchMemberStatus(
	string JobId,
	string JobName,
	string QueueName,
	JobState State,
	int Attempt,
	DateTimeOffset CreatedAt,
	DateTimeOffset? CompletedAt,
	string? LastError
);

/// <summary>A batch dependency graph.</summary>
/// <param name="BatchId">The opaque batch identifier.</param>
/// <param name="Nodes">The job nodes in the graph.</param>
/// <param name="Edges">The dependency edges in the graph.</param>
public sealed record BatchGraph(
	string BatchId,
	IReadOnlyList<BatchGraphNode> Nodes,
	IReadOnlyList<BatchGraphEdge> Edges
);

/// <summary>A job node in a batch graph.</summary>
/// <param name="JobId">The invocation identifier.</param>
/// <param name="JobName">The stable job name.</param>
/// <param name="State">The current invocation state.</param>
public sealed record BatchGraphNode(string JobId, string JobName, JobState State);

/// <summary>A dependency edge in a batch graph.</summary>
/// <param name="ChildJobId">The waiting child invocation identifier.</param>
/// <param name="ParentJobId">The parent invocation identifier for a job-to-job dependency.</param>
/// <param name="ParentBatchId">The parent batch identifier for a batch-to-job dependency.</param>
/// <param name="Trigger">The condition under which the edge is satisfied.</param>
public sealed record BatchGraphEdge(
	string ChildJobId,
	string? ParentJobId,
	string? ParentBatchId,
	ContinuationTrigger Trigger
);

/// <summary>Monitoring data for one job.</summary>
/// <param name="JobId">The invocation identifier.</param>
/// <param name="JobName">The stable job name.</param>
/// <param name="QueueName">The persisted queue name.</param>
/// <param name="State">The current invocation state.</param>
/// <param name="Attempt">The number of execution attempts already started.</param>
/// <param name="MaxAttempts">The total permitted execution attempts, if the job definition is available.</param>
/// <param name="CreatedAt">The UTC invocation-creation time.</param>
/// <param name="DueAt">The UTC time at which the invocation may be acquired.</param>
/// <param name="CompletedAt">The UTC terminal completion time, if any.</param>
/// <param name="LastError">The latest failure details, if any.</param>
/// <param name="BatchId">The containing batch identifier, if any.</param>
/// <param name="DependsOn">The invocation's incoming dependency edges.</param>
public sealed record JobStatus(
	string JobId,
	string JobName,
	string QueueName,
	JobState State,
	int Attempt,
	int? MaxAttempts,
	DateTimeOffset CreatedAt,
	DateTimeOffset DueAt,
	DateTimeOffset? CompletedAt,
	string? LastError,
	string? BatchId,
	IReadOnlyList<BatchGraphEdge> DependsOn
);

/// <summary>Read-only batch monitoring.</summary>
public interface IJobBatchMonitor
{
	/// <summary>Gets aggregate batch progress.</summary>
	/// <param name="batchId">The batch identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the monitoring operation.</param>
	/// <returns>The aggregate batch status, or <see langword="null"/> when the batch does not exist.</returns>
	ValueTask<BatchStatus?> GetStatusAsync(string batchId, CancellationToken cancellationToken = default);
	/// <summary>Queries batch members.</summary>
	/// <param name="batchId">The batch identifier.</param>
	/// <param name="query">The member filters and paging options.</param>
	/// <param name="cancellationToken">A token that can cancel the monitoring operation.</param>
	/// <returns>The batch members matching the query.</returns>
	ValueTask<IReadOnlyList<BatchMemberStatus>> QueryMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	);
	/// <summary>Gets the persisted dependency graph.</summary>
	/// <param name="batchId">The batch identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the monitoring operation.</param>
	/// <returns>The batch dependency graph, or <see langword="null"/> when the batch does not exist.</returns>
	ValueTask<BatchGraph?> GetGraphAsync(string batchId, CancellationToken cancellationToken = default);
}

/// <summary>Read-only single-job monitoring.</summary>
public interface IJobMonitor
{
	/// <summary>Gets one job and its incoming dependencies.</summary>
	/// <param name="jobId">The invocation identifier.</param>
	/// <param name="cancellationToken">A token that can cancel the monitoring operation.</param>
	/// <returns>The job status, or <see langword="null"/> when the invocation does not exist.</returns>
	ValueTask<JobStatus?> GetJobAsync(string jobId, CancellationToken cancellationToken = default);
}

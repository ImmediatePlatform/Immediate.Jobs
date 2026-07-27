namespace Immediate.Jobs.Shared;

/// <summary>An opaque reference to a durable job invocation.</summary>
public readonly struct JobHandle : IEquatable<JobHandle>
{
	/// <summary>Creates a handle for an existing invocation identifier.</summary>
	public JobHandle(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		Id = id;
	}

	internal JobHandle(string id, JobBatch? batch)
		: this(id) => Batch = batch;

	/// <summary>The opaque invocation identifier.</summary>
	public string Id { get; }

	internal JobBatch? Batch { get; }

	/// <inheritdoc />
	public bool Equals(JobHandle other) => string.Equals(Id, other.Id, StringComparison.Ordinal);

	/// <inheritdoc />
	public override bool Equals(object? obj) => obj is JobHandle other && Equals(other);

	/// <inheritdoc />
	public override int GetHashCode() => Id is null ? 0 : StringComparer.Ordinal.GetHashCode(Id);

	/// <summary>Compares two handles by their opaque invocation identifier.</summary>
	public static bool operator ==(JobHandle left, JobHandle right) => left.Equals(right);

	/// <summary>Compares two handles by their opaque invocation identifier.</summary>
	public static bool operator !=(JobHandle left, JobHandle right) => !left.Equals(right);

	/// <inheritdoc />
	public override string ToString() => Id ?? string.Empty;
}

/// <summary>An opaque reference to a committed atomic batch.</summary>
public sealed record BatchHandle
{
	/// <summary>Creates a handle for an existing batch identifier.</summary>
	public BatchHandle(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		Id = id;
	}

	/// <summary>The opaque batch identifier.</summary>
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
	public required string Id { get; init; }
	/// <summary>UTC time at which the batch was created.</summary>
	public required DateTimeOffset CreatedAt { get; init; }
	/// <summary>Total members added to the batch.</summary>
	public required int TotalJobs { get; init; }
	/// <summary>Members that have not reached a terminal state.</summary>
	public required int PendingCount { get; init; }
	/// <summary>Members that completed successfully.</summary>
	public int SucceededCount { get; init; }
	/// <summary>Members that exhausted their attempts.</summary>
	public int FailedCount { get; init; }
	/// <summary>Members cancelled by an explicit action or dependency violation.</summary>
	public int CancelledCount { get; init; }
	/// <summary>UTC time at which the first member was acquired.</summary>
	public DateTimeOffset? StartedAt { get; init; }
	/// <summary>UTC terminal completion time.</summary>
	public DateTimeOffset? CompletedAt { get; init; }
	/// <summary>The aggregate lifecycle state.</summary>
	public required BatchState State { get; init; }
}

/// <summary>A durable dependency edge from a job or batch to a child job.</summary>
public sealed record JobContinuationEdge
{
	/// <summary>The waiting child invocation.</summary>
	public required string ChildJobId { get; init; }
	/// <summary>The parent invocation for a job-to-job dependency.</summary>
	public string? ParentJobId { get; init; }
	/// <summary>The parent batch for a batch-to-job dependency.</summary>
	public string? ParentBatchId { get; init; }
	/// <summary>The condition under which the edge is satisfied.</summary>
	public ContinuationTrigger Trigger { get; init; } = ContinuationTrigger.Success;
}

/// <summary>A continuation buffered during a running job attempt and committed only on success.</summary>
public sealed record JobContinuationAddition
{
	/// <summary>The fully serialized new invocation.</summary>
	public required JobRecord Job { get; init; }
	/// <summary>How the new invocation joins and modifies the current workflow.</summary>
	public required ContinuationOptions Options { get; init; }
	/// <summary>The dependency trigger from the running job to the new invocation.</summary>
	public ContinuationTrigger Trigger { get; init; } = ContinuationTrigger.Success;
}

/// <summary>Per-attempt buffer used by generated schedulers during job execution.</summary>
public sealed class JobExecutionBuffer
{
	private readonly List<JobContinuationAddition> _additions = [];

	internal void Add(JobContinuationAddition addition) => _additions.Add(addition);

	internal IReadOnlyList<JobContinuationAddition> Snapshot() => _additions.Count == 0 ? [] : [.. _additions];
}

/// <summary>Aggregate progress for an atomic batch.</summary>
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
);

/// <summary>Filters members returned from a batch.</summary>
public sealed record BatchMemberQuery
{
	/// <summary>Optional lifecycle-state filter.</summary>
	public JobState? State { get; init; }
	/// <summary>Number of members to skip.</summary>
	public int Skip { get; init; }
	/// <summary>Maximum members to return.</summary>
	public int Take { get; init; } = 100;
}

/// <summary>Filters batches for dashboard presentation.</summary>
public sealed record JobBatchQuery
{
	/// <summary>Optional aggregate-state filter.</summary>
	public BatchState? State { get; init; }
	/// <summary>Number of batches to skip.</summary>
	public int Skip { get; init; }
	/// <summary>Maximum batches to return.</summary>
	public int Take { get; init; } = 100;
}

/// <summary>Monitoring data for one batch member.</summary>
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
public sealed record BatchGraph(
	string BatchId,
	IReadOnlyList<BatchGraphNode> Nodes,
	IReadOnlyList<BatchGraphEdge> Edges
);

/// <summary>A job node in a batch graph.</summary>
public sealed record BatchGraphNode(string JobId, string JobName, JobState State);

/// <summary>A dependency edge in a batch graph.</summary>
public sealed record BatchGraphEdge(
	string ChildJobId,
	string? ParentJobId,
	string? ParentBatchId,
	ContinuationTrigger Trigger
);

/// <summary>Monitoring data for one job.</summary>
public sealed record JobStatus(
	string JobId,
	string JobName,
	string QueueName,
	JobState State,
	int Attempt,
	int MaxAttempts,
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
	ValueTask<BatchStatus?> GetStatusAsync(string batchId, CancellationToken cancellationToken = default);
	/// <summary>Queries batch members.</summary>
	ValueTask<IReadOnlyList<BatchMemberStatus>> QueryMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	);
	/// <summary>Gets the persisted dependency graph.</summary>
	ValueTask<BatchGraph?> GetGraphAsync(string batchId, CancellationToken cancellationToken = default);
}

/// <summary>Read-only single-job monitoring.</summary>
public interface IJobMonitor
{
	/// <summary>Gets one job and its incoming dependencies.</summary>
	ValueTask<JobStatus?> GetJobAsync(string jobId, CancellationToken cancellationToken = default);
}

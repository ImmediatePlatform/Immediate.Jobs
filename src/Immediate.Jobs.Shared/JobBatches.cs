namespace Immediate.Jobs.Shared;

/// <summary>Creates atomic batches of typed generated jobs.</summary>
public interface IJobBatchScheduler
{
	/// <summary>Begins an in-memory batch buffer.</summary>
	/// <returns>The new batch buffer.</returns>
	JobBatch Begin();

	/// <summary>Begins a follow-up batch whose root members wait for a prior batch.</summary>
	/// <param name="after">The batch that must reach a terminal state before the follow-up roots are released.</param>
	/// <param name="on">The parent-batch outcome that releases the follow-up roots.</param>
	/// <returns>The new follow-up batch buffer.</returns>
	JobBatch Begin(BatchHandle after, ContinuationTrigger on = ContinuationTrigger.Success);

	/// <summary>Runs a batch body and commits it when the body succeeds.</summary>
	/// <param name="body">The callback that adds jobs and dependencies to the batch.</param>
	/// <param name="cancellationToken">A token that can cancel the commit operation.</param>
	/// <returns>A handle for the committed batch.</returns>
	ValueTask<BatchHandle> RunAsync(
		Func<JobBatch, ValueTask> body,
		CancellationToken cancellationToken = default
	);
}

/// <summary>Default scoped atomic-batch scheduler.</summary>
/// <param name="storage">The storage provider used to persist batch graphs.</param>
/// <param name="timeProvider">The clock used to timestamp batches and jobs.</param>
/// <param name="idGenerator">The generator used to create batch and job identifiers.</param>
public sealed class JobBatchScheduler(
	IJobStorage storage,
	TimeProvider timeProvider,
	IIdGenerator idGenerator
) : IJobBatchScheduler
{
	/// <inheritdoc />
	public JobBatch Begin() =>
		new(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			after: null,
			ContinuationTrigger.Success
		);

	/// <inheritdoc />
	public JobBatch Begin(BatchHandle after, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(after);
		return new(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			after,
			on
		);
	}

	/// <inheritdoc />
	public async ValueTask<BatchHandle> RunAsync(
		Func<JobBatch, ValueTask> body,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(body);
		await using var batch = Begin();
		await body(batch).ConfigureAwait(false);
		return await batch.CommitAsync(cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>An in-progress atomic batch buffer created by <see cref="IJobBatchScheduler"/>.</summary>
public sealed class JobBatch : IAsyncDisposable
{
	private enum Lifecycle
	{
		Open,
		Committing,
		Finished,
		Disposed,
	}

	private readonly Lock _gate = new();
	private readonly List<JobRecord> _jobs = [];
	private readonly List<JobContinuationEdge> _edges = [];
	private readonly IJobGraphStorage _storage;
	private readonly TimeProvider _timeProvider;
	private readonly BatchHandle? _after;
	private readonly ContinuationTrigger _trigger;
	private Lifecycle _lifecycle;

	internal JobBatch(
		IJobGraphStorage storage,
		TimeProvider timeProvider,
		IIdGenerator idGenerator,
		BatchHandle? after,
		ContinuationTrigger trigger
	)
	{
		_storage = storage;
		_timeProvider = timeProvider;
		_after = after;
		_trigger = trigger;
		Id = idGenerator.CreateId(IdKind.Batch);
	}

	/// <summary>The client-generated batch identifier.</summary>
	/// <value>The identifier assigned to the batch.</value>
	public string Id { get; }

	internal JobHandle Add(JobRecord record, ReadOnlySpan<JobHandle> parents, ContinuationTrigger on)
	{
		lock (_gate)
		{
			EnsureOpenCore();
			if (parents.IsEmpty)
			{
				_jobs.Add(record with { BatchId = Id });
				return new(record.Id, this);
			}

			var parentIds = new HashSet<string>(StringComparer.Ordinal);
			foreach (var parent in parents)
			{
				if (string.IsNullOrWhiteSpace(parent.Id))
					throw new ImmediateJobException("Continuation parent handles must have a non-empty identifier.");
				if (!ReferenceEquals(parent.Batch, this))
					throw new ImmediateJobException("Continuation handles must belong to the same open batch.");
				if (!parentIds.Add(parent.Id))
					throw new ImmediateJobException($"Duplicate continuation parent '{parent.Id}'.");
			}

			_jobs.Add(record with
			{
				BatchId = Id,
				State = JobState.AwaitingContinuation,
				RemainingDependencies = parentIds.Count,
			});
			foreach (var parentId in parentIds)
			{
				_edges.Add(new()
				{
					ChildJobId = record.Id,
					ParentJobId = parentId,
					Trigger = on,
				});
			}

			return new(record.Id, this);
		}
	}

	/// <summary>Atomically persists the buffered jobs and dependencies.</summary>
	/// <param name="cancellationToken">A token that can cancel the commit operation.</param>
	/// <returns>A handle for the committed batch.</returns>
	public async ValueTask<BatchHandle> CommitAsync(CancellationToken cancellationToken = default)
	{
		JobBatchRecord record;
		IReadOnlyList<JobRecord> jobs;
		IReadOnlyList<JobContinuationEdge> edges;
		lock (_gate)
		{
			EnsureOpenCore();
			if (_jobs.Count == 0)
				throw new ImmediateJobException("An atomic batch cannot be committed without jobs.");

			if (_after is { } parentBatch)
			{
				var children = _jobs.Select(static job => job.Id).ToHashSet(StringComparer.Ordinal);
				foreach (var edge in _edges)
					_ = children.Remove(edge.ChildJobId);

				foreach (var childId in children)
				{
					var index = _jobs.FindIndex(job => string.Equals(job.Id, childId, StringComparison.Ordinal));
					var job = _jobs[index];
					_jobs[index] = job with
					{
						State = JobState.AwaitingContinuation,
						RemainingDependencies = job.RemainingDependencies + 1,
					};
					_edges.Add(new()
					{
						ChildJobId = childId,
						ParentBatchId = parentBatch.Id,
						Trigger = _trigger,
					});
				}
			}

			record = new JobBatchRecord
			{
				Id = Id,
				CreatedAt = _timeProvider.GetUtcNow(),
				TotalJobs = _jobs.Count,
				PendingCount = _jobs.Count,
				State = BatchState.Executing,
			};
			jobs = Array.AsReadOnly(_jobs.ToArray());
			edges = Array.AsReadOnly(_edges.ToArray());
			_lifecycle = Lifecycle.Committing;
		}

		try
		{
			await _storage.EnqueueBatchAsync(record, jobs, edges, cancellationToken).ConfigureAwait(false);
			return new(Id);
		}
		finally
		{
			lock (_gate)
			{
				if (_lifecycle == Lifecycle.Committing)
					_lifecycle = Lifecycle.Finished;
			}
		}
	}

	/// <inheritdoc />
	public ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_lifecycle == Lifecycle.Disposed)
				return ValueTask.CompletedTask;
			_lifecycle = Lifecycle.Disposed;
			_jobs.Clear();
			_edges.Clear();
		}

		return ValueTask.CompletedTask;
	}

	internal void EnsureOpen()
	{
		lock (_gate)
			EnsureOpenCore();
	}

	private void EnsureOpenCore()
	{
		if (_lifecycle != Lifecycle.Open)
			throw new ImmediateJobException("A batch or one of its handles was used after commit or disposal.");
	}
}

/// <summary>Storage-backed implementation of the public monitoring services.</summary>
/// <param name="storage">The storage provider queried for job and batch status.</param>
/// <param name="definitions">The generated job definitions used to enrich monitoring results.</param>
public sealed class JobMonitor(IJobStorage storage, IEnumerable<JobDefinition> definitions) : IJobBatchMonitor, IJobMonitor
{
	/// <inheritdoc />
	public ValueTask<BatchStatus?> GetStatusAsync(string batchId, CancellationToken cancellationToken = default) =>
		JobStorageCapabilityGuards.RequireGraph(storage).GetBatchStatusAsync(batchId, cancellationToken);

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<BatchMemberStatus>> QueryMembersAsync(
		string batchId,
		BatchMemberQuery query,
		CancellationToken cancellationToken = default
	) => JobStorageCapabilityGuards.RequireGraph(storage).QueryBatchMembersAsync(batchId, query, cancellationToken);

	/// <inheritdoc />
	public ValueTask<BatchGraph?> GetGraphAsync(string batchId, CancellationToken cancellationToken = default) =>
		JobStorageCapabilityGuards.RequireGraph(storage).GetBatchGraphAsync(batchId, cancellationToken);

	/// <inheritdoc />
	public async ValueTask<JobStatus?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
	{
		var status = await storage.GetJobStatusAsync(jobId, cancellationToken).ConfigureAwait(false);
		if (status is null)
			return null;
		var definition = definitions.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, status.JobName, StringComparison.Ordinal));
		return definition is null ? status : status with { MaxAttempts = definition.MaxAttempts };
	}
}

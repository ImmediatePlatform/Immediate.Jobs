namespace Immediate.Jobs.Shared;

/// <summary>An in-progress atomic batch buffer.</summary>
public interface IJobBatch : IAsyncDisposable
{
	/// <summary>The client-generated batch identifier.</summary>
	string Id { get; }

	/// <summary>Atomically persists the buffered jobs and dependencies.</summary>
	ValueTask<BatchHandle> CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates atomic batches of typed generated jobs.</summary>
public interface IJobBatchScheduler
{
	/// <summary>Begins an in-memory batch buffer.</summary>
	IJobBatch Begin();

	/// <summary>Begins a follow-up batch whose root members wait for a prior batch.</summary>
	IJobBatch Begin(BatchHandle after, ContinuationTrigger on = ContinuationTrigger.Success);

	/// <summary>Runs a batch body and commits it when the body succeeds.</summary>
	ValueTask<BatchHandle> RunAsync(
		Func<IJobBatch, ValueTask> body,
		CancellationToken cancellationToken = default
	);
}

/// <summary>Default scoped atomic-batch scheduler.</summary>
public sealed class JobBatchScheduler(
	IJobStorage storage,
	TimeProvider timeProvider,
	IIdGenerator idGenerator
) : IJobBatchScheduler
{
	/// <inheritdoc />
	public IJobBatch Begin() =>
		new JobBatch(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			after: null,
			ContinuationTrigger.Success
		);

	/// <inheritdoc />
	public IJobBatch Begin(BatchHandle after, ContinuationTrigger on = ContinuationTrigger.Success)
	{
		ArgumentNullException.ThrowIfNull(after);
		return new JobBatch(
			JobStorageCapabilityGuards.RequireGraph(storage),
			timeProvider,
			idGenerator,
			after,
			on
		);
	}

	/// <inheritdoc />
	public async ValueTask<BatchHandle> RunAsync(
		Func<IJobBatch, ValueTask> body,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(body);
		await using var batch = Begin();
		await body(batch).ConfigureAwait(false);
		return await batch.CommitAsync(cancellationToken).ConfigureAwait(false);
	}
}

internal sealed class JobBatch(
	IJobGraphStorage storage,
	TimeProvider timeProvider,
	IIdGenerator idGenerator,
	BatchHandle? after,
	ContinuationTrigger trigger
) : IJobBatch
{
	private readonly List<JobRecord> _jobs = [];
	private readonly List<JobContinuationEdge> _edges = [];
	private bool _finished;

	public string Id { get; } = idGenerator.CreateId(IdKind.Batch);

	internal JobHandle Add(JobRecord record, ReadOnlySpan<JobHandle> parents, ContinuationTrigger on)
	{
		EnsureOpen();
		if (parents.IsEmpty)
		{
			_jobs.Add(record with { BatchId = Id });
			return new(record.Id, this);
		}

		var parentIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var parent in parents)
		{
			if (parent.Batch != this)
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

	public async ValueTask<BatchHandle> CommitAsync(CancellationToken cancellationToken = default)
	{
		EnsureOpen();
		if (_jobs.Count == 0)
			throw new ImmediateJobException("An atomic batch cannot be committed without jobs.");

		if (after is { } parentBatch)
		{
			var children = _jobs.Select(static job => job.Id).ToHashSet(StringComparer.Ordinal);
			foreach (var edge in _edges)
				_ = children.Remove(edge.ChildJobId);

			foreach (var childId in children)
			{
				var index = _jobs.FindIndex(job => job.Id == childId);
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
					Trigger = trigger,
				});
			}
		}

		var record = new JobBatchRecord
		{
			Id = Id,
			CreatedAt = timeProvider.GetUtcNow(),
			TotalJobs = _jobs.Count,
			PendingCount = _jobs.Count,
			State = BatchState.Executing,
		};
		await storage.EnqueueBatchAsync(record, _jobs, _edges, cancellationToken).ConfigureAwait(false);
		_finished = true;
		return new(Id);
	}

	public ValueTask DisposeAsync()
	{
		_finished = true;
		_jobs.Clear();
		_edges.Clear();
		return ValueTask.CompletedTask;
	}

	internal void EnsureOpen()
	{
		if (_finished)
			throw new ImmediateJobException("A batch or one of its handles was used after commit or disposal.");
	}
}

/// <summary>Storage-backed implementation of the public monitoring services.</summary>
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

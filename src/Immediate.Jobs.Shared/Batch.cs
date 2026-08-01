using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An in-progress atomic batch buffer created by <see cref="IBatchScheduler"/>.
/// </summary>
public sealed class Batch : IAsyncDisposable
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

	internal Batch(
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

	/// <summary>
	/// 	The client-generated batch identifier.
	/// </summary>
	/// <value>
	/// 	The identifier assigned to the batch.
	/// </value>
	public string Id { get; }

	internal JobHandle Add(JobRecord record, ReadOnlySpan<JobHandle> parents, ContinuationTrigger on)
	{
		lock (_gate)
		{
			EnsureOpenCore();
			if (parents.IsEmpty)
			{
				_jobs.Add(record with { BatchId = Id });
				return new(record.Id) { Batch = this };
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

			return new(record.Id) { Batch = this };
		}
	}

	/// <summary>
	/// 	Atomically persists the buffered jobs and dependencies.
	/// </summary>
	/// <param name="cancellationToken">
	/// 	A token that can cancel the commit operation.
	/// </param>
	/// <returns>
	/// 	A handle for the committed batch.
	/// </returns>
	public async ValueTask<BatchHandle> CommitAsync(CancellationToken cancellationToken = default)
	{
		BatchRecord record;
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

			record = new BatchRecord
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

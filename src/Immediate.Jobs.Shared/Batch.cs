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
		BatchId = new() { BatchId = idGenerator.CreateId(IdKind.Batch) };
	}

	/// <summary>
	/// 	The identifier assigned to the batch.
	/// </summary>
	public BatchHandle BatchId { get; }

	internal BatchJobHandle Add(JobRecord record)
	{
		lock (_gate)
		{
			EnsureOpenCore();
			_jobs.Add(record with { BatchId = BatchId });

			return new BatchJobHandle
			{
				Batch = this,
				Job = record.JobId,
			};
		}
	}

	internal BatchJobHandle Add(JobRecord record, IReadOnlyList<BatchJobHandle> parents, ContinuationTrigger on, TimeSpan delay)
	{
		lock (_gate)
		{
			EnsureOpenCore();

			var parentIds = new HashSet<JobHandle>();
			foreach (var parent in parents)
			{
				if (!ReferenceEquals(parent.Batch, this))
					throw new ImmediateJobException("Continuation handles must belong to the same open batch.");
				if (!parentIds.Add(parent.Job))
					throw new ImmediateJobException($"Duplicate continuation parent '{parent.Job.JobId}'.");
			}

			_jobs.Add(record with
			{
				BatchId = BatchId,
				State = JobState.AwaitingContinuation,
				RemainingDependencies = parentIds.Count,
			});

			foreach (var parentId in parentIds)
			{
				_edges.Add(new()
				{
					ChildJob = record.JobId,
					ParentJob = parentId,
					Trigger = on,
					Delay = delay,
				});
			}

			return new BatchJobHandle
			{
				Batch = this,
				Job = record.JobId,
			};
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
				var children = _jobs.Select(static job => job.JobId).ToHashSet(StringComparer.Ordinal);
				foreach (var edge in _edges)
					_ = children.Remove(edge.ChildJobId);

				foreach (var childId in children)
				{
					var index = _jobs.FindIndex(job => string.Equals(job.JobId, childId, StringComparison.Ordinal));
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
				Id = BatchId,
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
			return new(BatchId);
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

	private void EnsureOpenCore()
	{
		if (_lifecycle != Lifecycle.Open)
			throw new ImmediateJobException("A batch or one of its handles was used after commit or disposal.");
	}
}

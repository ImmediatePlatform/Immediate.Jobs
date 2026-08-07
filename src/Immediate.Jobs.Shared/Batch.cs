using System.Runtime.InteropServices;
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

	private readonly List<JobRecord> _jobs = [];
	private readonly List<JobRecord> _rootJobs = [];
	private readonly List<JobContinuationEdge> _edges = [];
	private readonly IJobGraphStorage _storage;
	private readonly TimeProvider _timeProvider;
	private readonly IReadOnlyList<BatchHandle>? _parents;
	private readonly ContinuationTrigger _trigger;
	private Lifecycle _lifecycle;

	internal Batch(
		IJobGraphStorage storage,
		TimeProvider timeProvider,
		IIdGenerator idGenerator,
		IReadOnlyList<BatchHandle>? parents,
		ContinuationTrigger trigger
	)
	{
		_storage = storage;
		_timeProvider = timeProvider;
		_parents = parents;
		_trigger = trigger;
		BatchId = new() { BatchId = idGenerator.CreateId(IdKind.Batch) };
	}

	/// <summary>
	/// 	The identifier assigned to the batch.
	/// </summary>
	public BatchHandle BatchId { get; }

	internal BatchJobHandle Add(JobRecord record)
	{
		EnsureOpenCore();

		_jobs.Add(record with { BatchId = BatchId });
		_rootJobs.Add(_jobs[^1]);

		return new BatchJobHandle
		{
			Batch = this,
			Job = record.JobId,
		};
	}

	internal BatchJobHandle Add(JobRecord record, IReadOnlyList<BatchJobHandle> parents, ContinuationTrigger on, TimeSpan delay)
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

		foreach (var parent in parents)
		{
			_edges.Add(new()
			{
				ChildJob = record.JobId,
				ParentJob = parent.Job,
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
		EnsureOpenCore();

		if (_jobs.Count == 0)
			throw new ImmediateJobException("An atomic batch cannot be committed without jobs.");

		if (_parents is { } parentBatches)
		{
			foreach (var parentBatch in parentBatches)
			{
				foreach (ref var job in CollectionsMarshal.AsSpan(_rootJobs))
				{
					job = job with
					{
						State = JobState.AwaitingContinuation,
						RemainingDependencies = job.RemainingDependencies + 1,
					};

					_edges.Add(new JobContinuationEdge
					{
						ChildJob = job.JobId,
						ParentBatch = parentBatch,
						Trigger = _trigger,
						Delay = TimeSpan.Zero,
					});
				}
			}
		}

		var record = new BatchRecord
		{
			BatchId = BatchId,
			CreatedAt = _timeProvider.GetUtcNow(),
			TotalJobs = _jobs.Count,
			PendingCount = _jobs.Count,
			State = BatchState.Executing,
		};

		_lifecycle = Lifecycle.Committing;

		try
		{
			await _storage.EnqueueBatchAsync(record, _jobs, _edges, cancellationToken).ConfigureAwait(false);
			return BatchId;
		}
		finally
		{
			if (_lifecycle == Lifecycle.Committing)
				_lifecycle = Lifecycle.Finished;
		}
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		_lifecycle = Lifecycle.Disposed;
	}

	private void EnsureOpenCore()
	{
		if (_lifecycle != Lifecycle.Open)
			ImmediateJobException.Throw("A batch or one of its handles was used after commit or disposal.");
	}
}

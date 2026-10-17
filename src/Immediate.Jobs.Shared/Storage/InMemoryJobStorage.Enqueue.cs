using System.Globalization;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask EnqueueAsync(JobSubmission job, CancellationToken cancellationToken = default)
	{
		EnqueueAsyncCalled(job.JobHandle);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		lock (_gate)
		{
			var record = CreateRecord(job);
			AddJob(record);
		}
	}

	/// <inheritdoc />
	public async ValueTask EnqueueContinuationAsync(JobSubmission job, IReadOnlyList<JobContinuationEdge> edges, CancellationToken cancellationToken = default)
	{
		EnqueueContinuationAsyncCalled(job.JobHandle, edges.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		lock (_gate)
		{
			var record = CreateRecord(job);
			ValidateNewJob(record);
			ValidateEdges([record], edges, batchHandle: null);
			AddJob(NormalizeWaitingJob(record, edges.Count));
			_edges.AddRange(edges);
			EvaluateAlreadyTerminalParents(edges);
		}
	}

	/// <inheritdoc />
	public async ValueTask EnqueueBatchAsync(BatchRecord batch, IReadOnlyList<JobSubmission> jobs, IReadOnlyList<JobContinuationEdge> edges, CancellationToken cancellationToken = default)
	{
		EnqueueBatchAsyncCalled(batch.BatchHandle, jobs.Count, edges.Count);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		lock (_gate)
		{
			var records = jobs.Select(CreateRecord).ToList();
			if (_batches.ContainsKey(batch.BatchHandle))
				throw new ImmediateJobException($"Batch '{batch.BatchHandle}' already exists.");
			ValidateBatchState(batch, records);
			var jobHandles = new HashSet<JobHandle>();
			foreach (var record in records)
			{
				ValidateNewJob(record);
				if (!jobHandles.Add(record.JobHandle))
					throw new ImmediateJobException($"Job '{record.JobHandle}' occurs more than once in the batch.");
				if (record.BatchHandle != batch.BatchHandle)
					throw new ImmediateJobException($"Job '{record.JobHandle}' does not belong to batch '{batch.BatchHandle}'.");
			}

			ValidateEdges(records, edges, batch.BatchHandle);
			_batches.Add(batch.BatchHandle, batch);
			var incomingCounts = edges.GroupBy(static edge => edge.ChildJobHandle)
				.ToDictionary(static group => group.Key, static group => group.Count());
			foreach (var record in records)
			{
				AddJob(incomingCounts.TryGetValue(record.JobHandle, out var dependencyCount)
					? NormalizeWaitingJob(record, dependencyCount)
					: record with { BatchHandle = batch.BatchHandle, RemainingDependencies = 0, FailedDependencies = 0 });
			}

			_edges.AddRange(edges);
			EvaluateAlreadyTerminalParents(edges);
		}
	}

	/// <inheritdoc />
	public async ValueTask AddBatchJobAsync(
		JobHandle currentJobHandle,
		int executionNumber,
		JobSubmission job,
		ContinuationOptions options,
		CancellationToken cancellationToken = default
	)
	{
		AddBatchJobAsyncCalled(job.JobHandle, executionNumber);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_jobs.TryGetValue(currentJobHandle, out var current) || current.State != JobState.Active)
				throw new ImmediateJobException($"Job '{currentJobHandle}' is not currently active.");
			if (current.Attempt != executionNumber)
			{
				throw new ImmediateJobException(string.Create(
					CultureInfo.InvariantCulture,
					$"Execution {executionNumber} cannot add a batch job for '{currentJobHandle}'; the active execution is {current.Attempt}."
				));
			}

			if (current.BatchHandle is null || !_batches.ContainsKey(current.BatchHandle))
				throw new ImmediateJobException("The current job does not belong to a batch.");
			if (options == ContinuationOptions.Detached)
				throw new ImmediateJobException("AddToBatchAsync(JobDetails, ...) cannot create detached work.");
			if (options is not (ContinuationOptions.BesideContinuations or ContinuationOptions.BeforeContinuations))
				throw new ArgumentOutOfRangeException(nameof(options));
			var persisted = CreateRecord(job);

			ValidateNewJob(persisted);
			if (persisted.BatchHandle != current.BatchHandle)
				throw new ImmediateJobException("The new persisted must belong to the current persisted's batch.");
			if (persisted.State is not (JobState.Pending or JobState.Scheduled))
				throw new ImmediateJobException($"Concurrent batch member '{persisted.JobHandle}' has invalid state '{persisted.State}'.");

			var existingWaiters = options == ContinuationOptions.BeforeContinuations
				? GetUnsettledWaiters(currentJobHandle)
				: [];
			ValidateSplice(existingWaiters, options == ContinuationOptions.BeforeContinuations ? 1 : 0);
			IncrementBatchMembers(current.BatchHandle, 1);
			AddJob(persisted with { RemainingDependencies = 0 });
			if (options == ContinuationOptions.BeforeContinuations)
				SpliceBeforeWaiters(persisted.JobHandle, existingWaiters);
		}
	}

	private JobRecord CreateRecord(JobSubmission job)
	{
		var definition = GetJobDefinition(job.JobName);
		return job.ToJobRecord(definition.Name);
	}

	internal JobDefinitionRecord GetJobDefinition(string name)
	{
		lock (_gate)
			return _jobDefinitions.TryGetValue(name, out var definition)
				? definition
				: throw new KeyNotFoundException($"Job definition '{name}' was not found.");
	}

	private void ValidateNewJob(JobRecord job)
	{
		if (_jobs.ContainsKey(job.JobHandle))
			throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
	}

	private void ValidateEdges(
		IReadOnlyList<JobRecord> newJobs,
		IReadOnlyList<JobContinuationEdge> edges,
		BatchHandle? batchHandle
	)
	{
		if (batchHandle is null && edges.Count == 0)
			throw new ImmediateJobException("A continuation must have at least one parent.");

		var newJobHandles = newJobs.Select(static job => job.JobHandle).ToHashSet();
		var logicalEdges = new HashSet<(JobHandle ChildJobHandle, string ParentKind, ContinuationHandle ParentJobHandle)>();

		var outgoing = newJobHandles.ToDictionary(static id => id, static _ => new List<JobHandle>());
		var incoming = newJobHandles.ToDictionary(static id => id, static _ => 0);

		foreach (var edge in edges)
		{
			if (!Enum.IsDefined(edge.Trigger))
				throw new ArgumentOutOfRangeException(nameof(edges), "Unknown continuation trigger.");
			if (!newJobHandles.Contains(edge.ChildJobHandle))
				throw new ImmediateJobException($"Continuation child '{edge.ChildJobHandle}' is not part of the atomic insert.");

			var hasJobParent = edge.ParentJobHandle is { };
			var hasBatchParent = edge.ParentBatchHandle is { };
			if (hasJobParent == hasBatchParent)
				throw new ImmediateJobException("A continuation edge must have exactly one job or batch parent.");

			if (hasJobParent)
			{
				var parentId = edge.ParentJobHandle!;
				if (parentId == edge.ChildJobHandle)
					throw new ImmediateJobException($"Continuation job '{edge.ChildJobHandle}' cannot depend on itself.");
				if (!newJobHandles.Contains(parentId) && !_jobs.ContainsKey(parentId))
					throw new KeyNotFoundException($"Continuation parent job '{parentId}' was not found.");
				if (!logicalEdges.Add((edge.ChildJobHandle, "job", parentId)))
					throw new ImmediateJobException($"Duplicate continuation edge '{parentId}' -> '{edge.ChildJobHandle}'.");

				if (newJobHandles.Contains(parentId))
				{
					outgoing[parentId].Add(edge.ChildJobHandle);
					incoming[edge.ChildJobHandle]++;
				}
			}
			else
			{
				var parentId = edge.ParentBatchHandle!;
				if (!_batches.ContainsKey(parentId))
					throw new KeyNotFoundException($"Continuation parent batch '{parentId}' was not found.");
				if (!logicalEdges.Add((edge.ChildJobHandle, "batch", parentId)))
					throw new ImmediateJobException($"Duplicate continuation edge from batch '{parentId}' to '{edge.ChildJobHandle}'.");
			}
		}

		var ready = new Queue<JobHandle>(incoming.Where(static pair => pair.Value == 0).Select(static pair => pair.Key));
		var visited = 0;
		while (ready.TryDequeue(out var parentId))
		{
			visited++;
			foreach (var childId in outgoing[parentId])
			{
				if (--incoming[childId] == 0)
					ready.Enqueue(childId);
			}
		}

		if (visited != newJobHandles.Count)
			throw new ImmediateJobException("The continuation graph contains a dependency cycle.");
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryEnqueueAsyncCalled,
		EventName = "Immediate.Jobs.Shared.EnqueueAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueAsync called (JobHandle={JobHandle})"
	)]
	private partial void EnqueueAsyncCalled(JobHandle jobHandle);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryEnqueueContinuationAsyncCalled,
		EventName = "Immediate.Jobs.Shared.EnqueueContinuationAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueContinuationAsync called (JobHandle={JobHandle}, Edges={Edges})"
	)]
	private partial void EnqueueContinuationAsyncCalled(JobHandle jobHandle, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryEnqueueBatchAsyncCalled,
		EventName = "Immediate.Jobs.Shared.EnqueueBatchAsyncCalled",
		Level = LogLevel.Debug,
		Message = "EnqueueBatchAsync called (BatchHandle={BatchHandle}, Jobs={Jobs}, Edges={Edges})"
	)]
	private partial void EnqueueBatchAsyncCalled(BatchHandle batchHandle, int jobs, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryAddBatchJobAsyncCalled,
		EventName = "Immediate.Jobs.Shared.AddBatchJobAsyncCalled",
		Level = LogLevel.Debug,
		Message = "AddBatchJobAsync called (JobHandle={JobHandle}, Execution={Execution})"
	)]
	private partial void AddBatchJobAsyncCalled(JobHandle jobHandle, int execution);
}

using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <summary>
	///	    Used for testing to pre-load various values to the storage before the test starts.
	/// </summary>
	/// <param name="jobs">
	///	    The jobs that should be loaded in the database.
	/// </param>
	/// <param name="batches">
	///	    The batches that should be loaded in the database.
	/// </param>
	/// <param name="edges">
	///	    The continuation edges that should be loaded in the database.
	/// </param>
	/// <param name="recurringSchedules">
	///	    The recurring schedules that should be loaded in the database.
	/// </param>
	/// <remarks>
	///	    This method should run before any other methods run to initialize test state. Use in regular app code is not
	///	    supported.
	/// </remarks>
	public void LoadPersistedJobState(
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<BatchRecord> batches,
		IReadOnlyList<JobContinuationEdge> edges,
		IReadOnlyList<RecurringJobSchedule> recurringSchedules
	)
	{
		lock (_gate)
		{
			var restoredJobs = new Dictionary<JobHandle, JobRecord>(_jobs);
			foreach (var job in jobs)
			{
				if (!restoredJobs.TryAdd(job.JobHandle, job))
					throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
			}

			var restoredBatches = new Dictionary<BatchHandle, BatchRecord>(_batches);
			foreach (var batch in batches)
			{
				if (!restoredBatches.TryAdd(batch.BatchHandle, batch))
					throw new ImmediateJobException($"Batch '{batch.BatchHandle}' already exists.");
			}

			var members = restoredJobs.Values.Where(static job => job.BatchHandle is not null)
				.ToLookup(static job => job.BatchHandle!);
			foreach (var group in members)
			{
				if (!restoredBatches.ContainsKey(group.Key))
					throw new ImmediateJobException($"Batch '{group.Key}' has members but no durable batch header.");
			}

			foreach (var batch in batches)
				ValidateBatchState(batch, members[batch.BatchHandle].ToList());

			var outgoing = restoredJobs.Keys.Cast<ContinuationHandle>().Concat(restoredBatches.Keys)
				.ToDictionary(static handle => handle, static _ => new List<ContinuationHandle>());
			var incomingCounts = outgoing.Keys.ToDictionary(static handle => handle, static _ => 0);
			// A batch completes only after all its members; include membership when detecting cycles.
			foreach (var job in restoredJobs.Values.Where(static job => job.BatchHandle is not null))
			{
				outgoing[job.JobHandle].Add(job.BatchHandle!);
				incomingCounts[job.BatchHandle!]++;
			}

			var restoredEdges = _edges.Concat(edges).ToList();
			var logicalEdges = new HashSet<(JobHandle Child, ContinuationHandle Parent)>();
			foreach (var edge in restoredEdges)
			{
				if (!Enum.IsDefined(edge.Trigger))
					throw new ArgumentOutOfRangeException(nameof(edges), "Unknown continuation trigger.");
				if (!restoredJobs.ContainsKey(edge.ChildJobHandle))
					throw new ImmediateJobException($"Continuation child '{edge.ChildJobHandle}' was not found.");
				if ((edge.ParentJobHandle is null) == (edge.ParentBatchHandle is null))
					throw new ImmediateJobException("A continuation edge must have exactly one job or batch parent.");
				var parent = (ContinuationHandle?)edge.ParentJobHandle ?? edge.ParentBatchHandle!;
				if (!outgoing.TryGetValue(parent, out var children))
					throw new ImmediateJobException($"Continuation parent '{parent}' was not found.");
				if (!logicalEdges.Add((edge.ChildJobHandle, parent)))
					throw new ImmediateJobException($"Duplicate continuation edge to '{edge.ChildJobHandle}' from '{parent}'.");
				children.Add(edge.ChildJobHandle);
				incomingCounts[edge.ChildJobHandle]++;
			}

			var incomingEdges = restoredEdges.ToLookup(static edge => edge.ChildJobHandle);
			foreach (var job in jobs)
			{
				var count = incomingEdges[job.JobHandle].Count();
				if (count == 0 && (job.State == JobState.AwaitingContinuation || job.RemainingDependencies != 0))
					throw new ImmediateJobException($"Job '{job.JobHandle}' has continuation dependencies but no durable dependency graph.");
				if (job.RemainingDependencies < 0 || job.RemainingDependencies > count || job.FailedDependencies < 0)
					throw new ImmediateJobException($"Job '{job.JobHandle}' has invalid continuation dependency counts.");
			}

			var ready = new Queue<ContinuationHandle>(incomingCounts.Where(static pair => pair.Value == 0).Select(static pair => pair.Key));
			var visited = 0;
			while (ready.TryDequeue(out var parent))
			{
				visited++;
				foreach (var child in outgoing[parent])
				{
					if (--incomingCounts[child] == 0)
						ready.Enqueue(child);
				}
			}

			if (visited != outgoing.Count)
				throw new ImmediateJobException("The persisted continuation graph contains a dependency cycle.");

			var scheduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var schedule in recurringSchedules)
			{
				if (!scheduleNames.Add(schedule.Name))
					throw new ImmediateJobException($"Recurring schedule '{schedule.Name}' occurs more than once.");
			}

			// Commit only after validating the complete graph. Persisted progress is already settled.
			foreach (var batch in batches)
				_batches.Add(batch.BatchHandle, batch);
			foreach (var job in jobs)
			{
				_jobs.Add(job.JobHandle, job with { QueueName = null });
				if (job.RecurringKey is { } recurringKey)
					_recurringKeys.Add(recurringKey);
			}

			_edges.AddRange(edges);
			foreach (var edge in edges)
			{
				if ((edge.ParentJobHandle is { } jobHandle && IsTerminal(restoredJobs[jobHandle].State))
					|| (edge.ParentBatchHandle is { } batchHandle && IsTerminal(restoredBatches[batchHandle].State)))
					_settledEdges.Add(edge);
			}

			foreach (var schedule in recurringSchedules)
				_recurring[schedule.Name] = schedule;
		}
	}

	private static void ValidateBatchState(BatchRecord batch, List<JobRecord> jobs)
	{
		if (jobs.Count == 0)
			throw new ImmediateJobException("An atomic batch cannot be empty.");

		var succeeded = jobs.Count(static job => job.State == JobState.Succeeded);
		var failed = jobs.Count(static job => job.State == JobState.Failed);
		var cancelled = jobs.Count(static job => job.State == JobState.Cancelled);
		var skipped = jobs.Count(static job => job.State == JobState.Skipped);
		var pending = jobs.Count - succeeded - failed - cancelled - skipped;

		var expectedState = true switch
		{
			_ when pending != 0 => BatchState.Executing,
			_ when failed != 0 => BatchState.Failed,
			_ when cancelled != 0 => BatchState.Cancelled,
			_ => BatchState.Succeeded,
		};

		if (
			batch.TotalJobs != jobs.Count ||
			batch.PendingCount != pending ||
			batch.SucceededCount != succeeded ||
			batch.FailedCount != failed ||
			batch.CancelledCount != cancelled ||
			batch.SkippedCount != skipped ||
			(batch.State != expectedState && !(expectedState == BatchState.Executing && batch.State == BatchState.WaitingForTrigger)) ||
			(pending == 0 != (batch.CompletedAt is not null))
		)
		{
			throw new ImmediateJobException("A batch header does not match its members or aggregate state.");
		}
	}
}

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
	///	    This method should run before any other methods run to initialize valid persisted test state. Use in regular
	///	    app code is not supported. Loading preserves persisted progress without validating the graph again.
	///	    The caller does not need to hold the <see cref="_gate"/> lock; this method acquires it internally.
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
			// Persisted progress is already settled; loading must not replay transitions.
			foreach (var batch in batches)
				ValidateNewElement(_batches, batch.BatchHandle) = batch;
			foreach (var job in jobs)
			{
				AddJob(job with { QueueName = null });
				if (job.RecurringKey is { } recurringKey)
					_recurringKeys.Add(recurringKey);
			}

			AddEdges(edges);
			foreach (var edge in edges)
			{
				if ((edge.ParentJobHandle is { } jobHandle && IsTerminal(_jobs[jobHandle].State))
					|| (edge.ParentBatchHandle is { } batchHandle && IsTerminal(_batches[batchHandle].State)))
					_settledEdges.Add(edge);
			}

			foreach (var schedule in recurringSchedules)
				_recurring[schedule.Name] = schedule;
		}
	}

	/// <summary>
	/// 	Loads persisted definitions into the fresh single-server primary without reconciling them.
	/// </summary>
	internal void LoadPersistedJobDefinitions(IReadOnlyList<JobDefinitionRecord> definitions)
	{
		lock (_gate)
		{
			foreach (var definition in definitions)
				_jobDefinitions.Add(definition.Name, definition);
		}
	}
}

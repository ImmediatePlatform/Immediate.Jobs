using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	private IEnumerable<JobRecord> GetJobsForBatch(BatchHandle batchHandle) =>
		_jobsByBatch.TryGetValue(batchHandle, out var handles) ? handles.Select(handle => _jobs[handle]) : [];

	private IEnumerable<JobRecord> GetJobsForDefinition(string jobName) =>
		_jobsByDefinition.TryGetValue(jobName, out var handles) ? handles.Select(handle => _jobs[handle]) : [];

	// Membership changes run under _gate. State-only updates keep their existing index entries.
	private void AddJob(JobRecord job)
	{
		if (!_jobs.TryAdd(job.JobHandle, job))
			throw new ImmediateJobException($"Job '{job.JobHandle}' already exists.");
		AddJobHandle(_jobsByDefinition, job.JobName, job.JobHandle);
		if (job.BatchHandle is { } batchHandle)
			AddJobHandle(_jobsByBatch, batchHandle, job.JobHandle);
	}

	private void SetJob(JobRecord job)
	{
		if (!_jobs.TryGetValue(job.JobHandle, out var previous))
		{
			AddJob(job);
			return;
		}

		if (!string.Equals(previous.JobName, job.JobName, StringComparison.OrdinalIgnoreCase))
		{
			RemoveJobHandle(_jobsByDefinition, previous.JobName, job.JobHandle);
			AddJobHandle(_jobsByDefinition, job.JobName, job.JobHandle);
		}

		if (previous.BatchHandle != job.BatchHandle)
		{
			if (previous.BatchHandle is { } oldBatch)
			{
				var handles = _jobsByBatch[oldBatch];
				handles.Remove(job.JobHandle);
				if (handles.Count == 0)
					_jobsByBatch.Remove(oldBatch);
			}

			if (job.BatchHandle is { } newBatch)
				AddJobHandle(_jobsByBatch, newBatch, job.JobHandle);
		}

		_jobs[job.JobHandle] = job;
	}

	private static void AddJobHandle(Dictionary<BatchHandle, List<JobHandle>> index, BatchHandle key, JobHandle handle)
	{
		if (!index.TryGetValue(key, out var handles))
			index.Add(key, handles = []);
		handles.Add(handle);
	}

	private static void AddJobHandle(Dictionary<string, HashSet<JobHandle>> index, string key, JobHandle handle)
	{
		if (!index.TryGetValue(key, out var handles))
			index.Add(key, handles = []);
		handles.Add(handle);
	}

	private static void RemoveJobHandle(Dictionary<string, HashSet<JobHandle>> index, string key, JobHandle handle)
	{
		var handles = index[key];
		handles.Remove(handle);
		if (handles.Count == 0)
			index.Remove(key);
	}

	private void RemoveJobs(HashSet<JobHandle> jobHandles)
	{
		if (jobHandles.Count == 0)
			return;
		var single = jobHandles.Count == 1 ? jobHandles.First() : null;
		var batches = new HashSet<BatchHandle>();
		foreach (var handle in jobHandles)
		{
			var job = _jobs[handle];
			RemoveJobHandle(_jobsByDefinition, job.JobName, handle);
			if (job.BatchHandle is { } batchHandle)
				batches.Add(batchHandle);
			_jobs.Remove(handle);
			_executions.Remove(handle);
			if (job.RecurringKey is { } recurringKey)
				_recurringKeys.Remove(recurringKey);
		}

		// Filter each affected list once, avoiding quadratic removal when a large group is purged.
		foreach (var batch in batches)
		{
			var handles = _jobsByBatch[batch];
			if (single is not null)
				handles.Remove(single);
			else
				handles.RemoveAll(jobHandles.Contains);
			if (handles.Count == 0)
				_jobsByBatch.Remove(batch);
		}
	}
}

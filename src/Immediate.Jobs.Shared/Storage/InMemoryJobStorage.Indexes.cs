using System.Runtime.InteropServices;
using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and enumeration of the returned sequence.
	/// </remarks>
	private IEnumerable<JobRecord> GetJobsForQueue(string queueName, IEnumerable<string>? definitionNames = null)
	{
		var definitions = (definitionNames ?? _jobsByDefinition.Keys)
			.Where(name => string.Equals(GetQueueName(name), queueName, StringComparison.Ordinal) && _jobsByDefinition.ContainsKey(name))
			.ToList();
		// Dictionary keys are distinct. When every stored definition matches, avoid resolving each handle.
		return definitions.Count == _jobsByDefinition.Count ? _jobs.Values : definitions.SelectMany(GetJobsForDefinition);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and enumeration of the returned sequence.
	/// </remarks>
	private IEnumerable<JobRecord> GetJobsForBatch(BatchHandle batchHandle) =>
		_jobsByBatch.TryGetValue(batchHandle, out var handles) ? handles.Select(handle => _jobs[handle]) : [];

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock throughout this call and enumeration of the returned sequence.
	/// </remarks>
	private IEnumerable<JobRecord> GetJobsForDefinition(string jobName) =>
		_jobsByDefinition.TryGetValue(jobName, out var handles) ? handles.Select(handle => _jobs[handle]) : [];

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void AddJob(JobRecord job)
	{
		ValidateNewElement(_jobs, job.JobHandle) = job;
		AddJobIndexes(job);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void AddJobIndexes(JobRecord job)
	{
		AddSchedulingIndexes(job);
		AddJobHandle(_jobsByDefinition, job.JobName, job.JobHandle);
		if (job.BatchHandle is { } batchHandle)
			AddJobHandle(_jobsByBatch, batchHandle, job.JobHandle);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void SetJob(JobRecord job)
	{
		ref var stored = ref CollectionsMarshal.GetValueRefOrAddDefault(_jobs, job.JobHandle, out var exists);
		if (exists)
			UpdateJob(ref stored!, job);
		else
		{
			stored = job;
			AddJobIndexes(job);
		}
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock while modifying the supplied storage index.
	/// </remarks>
	private static void AddJobHandle(Dictionary<BatchHandle, List<JobHandle>> index, BatchHandle key, JobHandle handle)
	{
		ref var handles = ref CollectionsMarshal.GetValueRefOrAddDefault(index, key, out _);
		handles ??= [];
		handles.Add(handle);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock while modifying the supplied storage index.
	/// </remarks>
	private static void AddJobHandle(Dictionary<string, HashSet<JobHandle>> index, string key, JobHandle handle)
	{
		ref var handles = ref CollectionsMarshal.GetValueRefOrAddDefault(index, key, out _);
		handles ??= [];
		handles.Add(handle);
	}

	/// <remarks>
	/// 	The caller must hold the <see cref="_gate"/> lock.
	/// </remarks>
	private void RemoveJobs(HashSet<JobHandle> jobHandles)
	{
		if (jobHandles.Count == 0)
			return;
		var single = jobHandles.Count == 1 ? jobHandles.First() : null;
		var batches = new HashSet<BatchHandle>();
		foreach (var handle in jobHandles)
		{
			var job = _jobs[handle];
			RemoveSchedulingIndexes(job);
			var definitions = _jobsByDefinition[job.JobName];
			definitions.Remove(handle);
			if (definitions.Count == 0)
				_jobsByDefinition.Remove(job.JobName);
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

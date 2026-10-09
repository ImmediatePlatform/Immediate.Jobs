using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared.Internals;

internal static class JobDefinitionResolver
{
	internal static async ValueTask<JobDefinitionRecord> GetDefinitionAsync(
		IJobStorage storage,
		string jobName,
		CancellationToken cancellationToken
	)
	{
		var definitions = await storage.GetJobDefinitionsAsync(cancellationToken);
		return definitions.FirstOrDefault(definition => string.Equals(definition.Name, jobName, StringComparison.OrdinalIgnoreCase))
			?? throw new KeyNotFoundException($"Job definition '{jobName}' was not found.");
	}

	internal static async ValueTask<JobRecord> ResolveAsync(
		IJobStorage storage,
		JobRecord job,
		CancellationToken cancellationToken
	)
	{
		var definition = await GetDefinitionAsync(storage, job.JobName, cancellationToken);
		return job with { JobName = definition.Name, QueueName = definition.QueueName };
	}

	internal static async ValueTask<IReadOnlyList<JobRecord>> ResolveAsync(
		IJobStorage storage,
		IReadOnlyList<JobRecord> jobs,
		CancellationToken cancellationToken
	)
	{
		if (!jobs.Any(static job => job.QueueName.Length == 0))
			return jobs;

		var definitions = (await storage.GetJobDefinitionsAsync(cancellationToken))
			.ToDictionary(static definition => definition.Name, StringComparer.OrdinalIgnoreCase);
		return jobs.Select(job =>
		{
			if (job.QueueName.Length > 0)
				return job;
			if (!definitions.TryGetValue(job.JobName, out var definition))
				throw new KeyNotFoundException($"Job definition '{job.JobName}' was not found.");
			return job with { JobName = definition.Name, QueueName = definition.QueueName };
		}).ToList();
	}
}

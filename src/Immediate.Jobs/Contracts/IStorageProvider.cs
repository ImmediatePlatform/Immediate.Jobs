using Immediate.Jobs.Models;

namespace Immediate.Jobs.Contracts;

public interface IStorageProvider
{
	Task<IReadOnlyList<Job>> GetJobs(JobSearchParameters searchParameters);
	Task<string> EnqueueJob(Job job, JobServer? jobServer);
	Task<bool> ClaimJob(Job job, JobServer jobServer);
	Task CompleteJob(Job job);
	Task FailJob(Job job, Exception exception);
	Task CancelJob(Job job);

	Task<IReadOnlyList<JobServer>> GetServers();
	Task EnlistServer(JobServer server);
	Task DelistServer(JobServer server);
}

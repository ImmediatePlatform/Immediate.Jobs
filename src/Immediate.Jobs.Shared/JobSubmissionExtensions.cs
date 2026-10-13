using Immediate.Jobs.Shared.Apis;

namespace Immediate.Jobs.Shared;

internal static class JobSubmissionExtensions
{
	public static JobRecord CreateRecord(this JobSubmission job, string name) => new()
	{
		JobHandle = job.JobHandle,
		JobName = name,
		Payload = job.Payload,
		Context = job.Context,
		GroupId = job.GroupId,
		State = job.State,
		DueAt = job.DueAt,
		CreatedAt = job.CreatedAt,
		RecurringKey = job.RecurringKey,
		TraceParent = job.TraceParent,
		TraceState = job.TraceState,
		BatchHandle = job.BatchHandle,
		RemainingDependencies = job.RemainingDependencies,
	};
}

namespace Immediate.Jobs.Aspire.Api.Contracts;

public sealed record EnqueueJobResponse(string JobHandle, Uri DashboardUrl);

public sealed record FairQueueDemoResponse(
	Guid RunId,
	int BacklogJobs,
	string BacklogGroup,
	string QuietGroup,
	string QuietJobHandle,
	Uri DashboardUrl
);

public sealed record CreateContinuationBranchDemoResponse(
	Guid RunId,
	bool RootWillFail,
	string BatchHandle,
	string RootJobHandle,
	string SuccessJobHandle,
	string FailureJobHandle,
	Uri DashboardUrl,
	Uri StatusUrl
);

public sealed record CreateOrderBatchResponse(
	Guid OrderId,
	string BatchHandle,
	int InitialJobs,
	int ExpectedJobsAfterExpansion,
	Uri DashboardUrl,
	Uri StatusUrl
);

public sealed record CreateGameReleaseBatchResponse(
	Guid ReleaseId,
	string Title,
	string BatchHandle,
	int JobCount,
	Uri DashboardUrl,
	Uri StatusUrl
);

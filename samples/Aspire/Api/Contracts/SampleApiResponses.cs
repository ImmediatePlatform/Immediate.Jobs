namespace Immediate.Jobs.Aspire.Api.Contracts;

public sealed record EnqueueJobResponse(string JobId, Uri DashboardUrl);

public sealed record FairQueueDemoResponse(
	Guid RunId,
	int BacklogJobs,
	string BacklogGroup,
	string QuietGroup,
	string QuietJobId,
	Uri DashboardUrl
);

public sealed record CreateContinuationBranchDemoResponse(
	Guid RunId,
	bool RootWillFail,
	string BatchId,
	string RootJobId,
	string SuccessJobId,
	string FailureJobId,
	Uri DashboardUrl,
	Uri StatusUrl
);

public sealed record CreateOrderBatchResponse(
	Guid OrderId,
	string BatchId,
	int InitialJobs,
	int ExpectedJobsAfterExpansion,
	Uri DashboardUrl,
	Uri StatusUrl
);

public sealed record CreateGameReleaseBatchResponse(
	Guid ReleaseId,
	string Title,
	string BatchId,
	int JobCount,
	Uri DashboardUrl,
	Uri StatusUrl
);

namespace Immediate.Jobs.EntityFrameworkCore;

internal static class LibraryEventIds
{
	public const int DisposeAsyncCalled = 11500;
	public const int InitializeAsyncCalled = 11501;
	public const int LoadPersistedJobStateCalled = 11502;
	public const int EnqueueAsyncCalled = 11503;
	public const int EnqueueContinuationAsyncCalled = 11504;
	public const int EnqueueBatchAsyncCalled = 11505;
	public const int AcquireDueJobsAsyncCalled = 11506;
	public const int AcquireJobsAsyncCalled = 11507;
	public const int SetExecutionTelemetryAsyncCalled = 11508;
	public const int RenewLeaseAsyncCalled = 11509;
	public const int CompleteAsyncCalled = 11510;
	public const int CompleteWithContinuationsAsyncCalled = 11511;
	public const int AddBatchJobAsyncCalled = 11512;
	public const int FailAsyncCalled = 11513;
	public const int MergeRecurringSchedulesListAsyncCalled = 11514;
	public const int UpsertRecurringAsyncCalled = 11515;
	public const int RemoveRecurringAsyncCalled = 11516;
	public const int PauseRecurringAsyncCalled = 11517;
	public const int ResumeRecurringAsyncCalled = 11518;
	public const int GetDueRecurringAsyncCalled = 11519;
	public const int MaterializeRecurringAsyncCalled = 11520;
	public const int GetMonitoringSnapshotAsyncCalled = 11521;
	public const int QueryJobsAsyncCalled = 11522;
	public const int QueryNonCompletedJobsAsyncCalled = 11523;
	public const int QueryJobExecutionsAsyncCalled = 11524;
	public const int GetBatchStatusAsyncCalled = 11525;
	public const int GetIncomingEdgesAsyncCalled = 11526;
	public const int QueryBatchesAsyncCalled = 11527;
	public const int QueryBatchMembersAsyncCalled = 11528;
	public const int GetBatchGraphAsyncCalled = 11529;
	public const int GetJobStatusAsyncCalled = 11530;
	public const int CancelBatchAsyncCalled = 11531;
	public const int DeleteBatchAsyncCalled = 11532;
	public const int CancelAsyncCalled = 11533;
	public const int RetryAsyncCalled = 11534;
	public const int DeleteAsyncCalled = 11535;
	public const int PurgeJobsAsyncCalled = 11536;
	public const int PurgeBatchesAsyncCalled = 11537;
	public const int HeartbeatAsyncCalled = 11538;
	public const int IsHealthyAsyncCalled = 11539;
}

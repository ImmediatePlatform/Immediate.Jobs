namespace Immediate.Jobs.LinqToDB;

internal static class LibraryEventIds
{
	public const int DisposeAsyncCalled = 11600;
	public const int InitializeAsyncCalled = 11601;
	public const int LoadPersistedJobStateCalled = 11602;
	public const int EnqueueAsyncCalled = 11603;
	public const int GetIncomingEdgesAsyncCalled = 11604;
	public const int EnqueueContinuationAsyncCalled = 11605;
	public const int EnqueueBatchAsyncCalled = 11606;
	public const int AcquireDueJobsAsyncCalled = 11607;
	public const int AcquireJobsAsyncCalled = 11608;
	public const int SetExecutionTelemetryAsyncCalled = 11609;
	public const int RenewLeaseAsyncCalled = 11610;
	public const int CompleteAsyncCalled = 11611;
	public const int CompleteWithContinuationsAsyncCalled = 11612;
	public const int AddBatchJobAsyncCalled = 11613;
	public const int FailAsyncCalled = 11614;
	public const int MergeRecurringSchedulesListAsyncCalled = 11615;
	public const int UpsertRecurringAsyncCalled = 11616;
	public const int RemoveRecurringAsyncCalled = 11617;
	public const int PauseRecurringAsyncCalled = 11618;
	public const int ResumeRecurringAsyncCalled = 11619;
	public const int GetDueRecurringAsyncCalled = 11620;
	public const int MaterializeRecurringAsyncCalled = 11621;
	public const int GetMonitoringSnapshotAsyncCalled = 11622;
	public const int QueryJobsAsyncCalled = 11623;
	public const int QueryJobExecutionsAsyncCalled = 11624;
	public const int GetBatchStatusAsyncCalled = 11625;
	public const int QueryBatchesAsyncCalled = 11626;
	public const int QueryBatchMembersAsyncCalled = 11627;
	public const int GetBatchGraphAsyncCalled = 11628;
	public const int GetJobStatusAsyncCalled = 11629;
	public const int CancelBatchAsyncCalled = 11630;
	public const int DeleteBatchAsyncCalled = 11631;
	public const int CancelAsyncCalled = 11632;
	public const int RetryAsyncCalled = 11633;
	public const int DeleteAsyncCalled = 11634;
	public const int PurgeJobsAsyncCalled = 11635;
	public const int PurgeBatchesAsyncCalled = 11636;
	public const int HeartbeatAsyncCalled = 11637;
	public const int IsHealthyAsyncCalled = 11638;
}

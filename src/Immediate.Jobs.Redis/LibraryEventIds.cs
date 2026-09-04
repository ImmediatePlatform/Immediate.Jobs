namespace Immediate.Jobs.Redis;

internal static class LibraryEventIds
{
	public const int InitializeAsyncCalled = 11700;
	public const int EnqueueAsyncCalled = 11701;
	public const int AcquireDueJobsAsyncCalled = 11702;
	public const int SetExecutionTelemetryAsyncCalled = 11703;
	public const int RenewLeaseAsyncCalled = 11704;
	public const int CompleteAsyncCalled = 11705;
	public const int FailAsyncCalled = 11706;
	public const int GetMonitoringSnapshotAsyncCalled = 11707;
	public const int QueryJobsAsyncCalled = 11708;
	public const int QueryJobExecutionsAsyncCalled = 11709;
	public const int QueryNonCompletedJobsAsyncCalled = 11710;
	public const int GetJobStatusAsyncCalled = 11711;
	public const int CancelAsyncCalled = 11712;
	public const int RetryAsyncCalled = 11713;
	public const int DeleteAsyncCalled = 11714;
	public const int PurgeJobsAsyncCalled = 11715;
	public const int HeartbeatAsyncCalled = 11716;
	public const int IsHealthyAsyncCalled = 11717;
	public const int MergeRecurringSchedulesListAsyncCalled = 11718;
	public const int UpsertRecurringAsyncCalled = 11719;
	public const int RemoveRecurringAsyncCalled = 11720;
	public const int PauseRecurringAsyncCalled = 11721;
	public const int ResumeRecurringAsyncCalled = 11722;
	public const int GetDueRecurringAsyncCalled = 11723;
	public const int MaterializeRecurringAsyncCalled = 11724;
	public const int DisposeAsyncCalled = 11725;
	public const int MaterializeRecurringAsyncCalledWithDependencies = 11726;
}

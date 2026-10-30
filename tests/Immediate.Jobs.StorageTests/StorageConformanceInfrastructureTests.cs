using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing.Storage;

namespace Immediate.Jobs.StorageTests;

public sealed class StorageConformanceInfrastructureTests
{
	private const StorageCapabilities AllCapabilities =
		StorageCapabilities.Queue |
		StorageCapabilities.Graph;

	public static TheoryData<StorageCapabilities, bool, bool, bool, string[]> Selections => new()
	{
		{ StorageCapabilities.Queue, false, false, false, [.. QueueCases, .. ProcessOwnershipCases] },
		{ StorageCapabilities.Queue, false, false, true, [.. QueueCases, .. ProcessOwnershipCases] },
		{ StorageCapabilities.Queue, false, true, false, [.. QueueCases, .. LeaseCases] },
		{ StorageCapabilities.Queue, false, true, true, [.. QueueCases, .. LeaseCases, .. QueueInterruptionCases] },
		{ AllCapabilities, false, false, false, [.. QueueCases, .. GraphCases, .. ProcessOwnershipCases] },
		{ AllCapabilities, false, false, true, [.. QueueCases, .. GraphCases, .. ProcessOwnershipCases] },
		{ AllCapabilities, false, true, false, [.. QueueCases, .. GraphCases, .. LeaseCases] },
		{
			AllCapabilities, false, true, true,
			[.. QueueCases, .. GraphCases, .. LeaseCases, .. QueueInterruptionCases, .. GraphInterruptionCases]
		},
		{ AllCapabilities, true, true, false, [.. QueueCases, .. GraphCases, .. LeaseCases, .. ReplicaCases] },
		{
			AllCapabilities, true, true, true,
			[.. QueueCases, .. GraphCases, .. LeaseCases, .. QueueInterruptionCases, .. GraphInterruptionCases, .. ReplicaCases]
		},
	};

	[Theory]
	[MemberData(nameof(Selections))]
	public void GetCasesSelectsExpectedNames(
		StorageCapabilities capabilities,
		bool includeSingleServerReplicaCases,
		bool usesExpiringLeases,
		bool includeLeaseInterruptionCases,
		string[] expectedNames
	)
	{
		var cases = JobStorageConformanceSuite.GetCases(
			capabilities,
			includeSingleServerReplicaCases,
			usesExpiringLeases,
			includeLeaseInterruptionCases
		);

		Assert.Equal(
			expectedNames.Order(StringComparer.Ordinal),
			cases.Select(testCase => testCase.Name).Order(StringComparer.Ordinal)
		);
	}

	[Fact]
	public void AllCasesByNameMatchesExpectedCatalogue()
	{
		string[] expectedNames =
		[
			.. QueueCases,
			.. GraphCases,
			.. LeaseCases,
			.. ProcessOwnershipCases,
			.. QueueInterruptionCases,
			.. GraphInterruptionCases,
			.. ReplicaCases,
		];

		Assert.Equal(
			expectedNames.Order(StringComparer.Ordinal).Select(name => (Key: name, Name: name)),
			JobStorageConformanceSuite.AllCasesByName.OrderBy(pair => pair.Key, StringComparer.Ordinal)
				.Select(pair => (pair.Key, pair.Value.Name))
		);
	}

	[Theory]
	[InlineData(StorageCapabilities.None, false, true)]
	[InlineData(StorageCapabilities.Graph, false, true)]
	[InlineData((StorageCapabilities)2, false, true)]
	[InlineData((StorageCapabilities)32, false, true)]
	[InlineData(StorageCapabilities.Queue | (StorageCapabilities)32, false, true)]
	[InlineData(StorageCapabilities.Queue, true, true)]
	[InlineData(AllCapabilities, true, false)]
	public void GetCasesRejectsInvalidConfiguration(
		StorageCapabilities capabilities,
		bool includeSingleServerReplicaCases,
		bool usesExpiringLeases
	)
	{
		Assert.ThrowsAny<ArgumentException>(() => JobStorageConformanceSuite.GetCases(
			capabilities,
			includeSingleServerReplicaCases,
			usesExpiringLeases
		));
	}

	private static readonly string[] QueueCases =
	[
		"Definitions.Acquisition.EnforcesLimitsUnderContention",
		"Definitions.Acquisition.SkipsBlockedDefinitionsInFairQueues",
		"Definitions.Catalogue.PreservesMetadataWhenStartupIsCancelled",
		"Definitions.Catalogue.ReconcilesCaseInsensitiveNames",
		"Definitions.Catalogue.RoundTripsAndUpdatesConfiguration",
		"Definitions.Catalogue.SerializesConcurrentCompleteLists",
		"Definitions.Concurrency.RenewalAndEarlyRelease",
		"Definitions.Controls.RejectsMissingDefinitions",
		"Definitions.Controls.ResolvesCaseInsensitiveNamesAndPreservesLimits",
		"Definitions.FixedWindow.ResetsAtAlignedBoundary",
		"Definitions.Limits.CombineAndResumeWithoutReset",
		"Definitions.Monitoring.ReportsCurrentWindowCounts",
		"Definitions.Monitoring.ReportsPauseAndUnexpiredConcurrencyIndependently",
		"Definitions.Pause.BlocksPendingAndNewJobsOnly",
		"Definitions.Recurring.MaterializesDuringExecutionPause",
		"Definitions.Recurring.PreservesProgressAndInvocations",
		"Definitions.Removal.CancelsScopedJobsAndSchedulesAndFencesWorkers",
		"Definitions.Retries.CountSuccessfulAcquisitionsOnly",
		"Definitions.SlidingWindow.EnforcesExactBoundaryAndRetainsHistoryAfterPurge",
		"Definitions.SlidingWindow.ReleasesOnlyExpiredAcquisitions",
		"Definitions.Submission.UsesCurrentDefinition",
		"FairQueues.Cancellation.ClearsCancelledGroups",
		"FairQueues.Concurrency.ClaimsDistinctJobs",
		"FairQueues.Disabled.PreservesOrdinaryDueOrder",
		"FairQueues.NoisyNeighbors.ServesQuietGroupFirst",
		"FairQueues.Retries.KeepsRetriedJobsEligible",
		"FairQueues.Rotation.InterleavesGroupsWithinOneAcquisition",
		"FairQueues.Rotation.ServesNewGroupAheadOfServedBacklog",
		"Monitoring.Definitions.IncludesGlobalJobsAndDynamicRecurringConfiguration",
		"Monitoring.Snapshot.BulkStatusMatchesIndividualReadsAtBoundaries",
		"Queue.Acquisition.ClaimsEachJobOnceUnderContention",
		"Queue.Acquisition.DoesNotDuplicateClaimsAcrossRepeatedQueueEntries",
		"Queue.Acquisition.ExcludesFutureJobs",
		"Queue.Acquisition.HonorsOrderingAndCapacities",
		"Queue.Cancellation.ObservesPreCancelledOperation",
		"Queue.Enqueue.RoundTripsRecordAndRejectsDuplicate",
		"Queue.Executions.PersistsFailureRetryAndCancellation",
		"Queue.Executions.PersistsTelemetryCompletionAndHistory",
		"Queue.Health.ReportsProvisionedBackendReachable",
		"Queue.Lifecycle.InitializesIdempotently",
		"Queue.Lifecycle.ToleratesRepeatedConcurrentDisposal",
		"Queue.Maintenance.PurgesTerminalHistory",
		"Queue.Monitoring.ReportsCountsStatusAndHeartbeatLiveness",
		"Queue.Mutations.RejectsMissingAndInvalidTransitions",
		"Queue.Queries.ComposesFiltersAndPagesDeterministically",
		"Recurring.Definitions.AdoptsDynamicScheduleWhenOriginalDefinitionIsRemoved",
		"Recurring.Definitions.MergesCodeDefinedSchedulesAndPreservesDynamicSchedules",
		"Recurring.DueScanning.FiltersOrdersAndBatchesSchedules",
		"Recurring.Eligibility.MatchesCaseInsensitiveJobNamesBeforeBatching",
		"Recurring.Exceptions.DistinguishesMissingAndInvalidDashboardActions",
		"Recurring.Identity.MatchesCaseInsensitiveNamesThroughout",
		"Recurring.Lifecycle.UpdatesPausesResumesAndRemovesDynamicSchedule",
		"Recurring.Maintenance.PurgeRemovesOccurrenceDedupeState",
		"Recurring.Materialization.CreatesOccurrenceAndAdvancesScheduleAtomically",
		"Recurring.Materialization.DedupeHitStillAdvancesSchedule",
		"Recurring.Materialization.DeduplicatesConcurrentOccurrence",
		"Recurring.Materialization.PersistsSkippedOccurrence",
		"Recurring.Materialization.RejectsStaleDueEntry",
		"Recurring.Upsert.PreservesMaterializedProgressAcrossStaleAliases",
		"Tags.Catalogue.ReconcilesCaseInsensitiveNamesAcrossTagScopes",
		"Tags.Catalogue.ReconcilesCompleteListsWithinTagScope",
		"Tags.Catalogue.RemovesPreviouslyMatchingRetaggedDefinitions",
		"Tags.Catalogue.SerializesConcurrentCompleteLists",
		"Tags.Recurring.FiltersEligibleNamesBeforeBatchLimit",
		"Tags.Recurring.PreservesOtherScopesProgressAndInvocations",
		"Tags.Recurring.RemovesCodeScheduleWhenCronRemovedWhileRetagging",
		"Trigger.Parameters.UpdatesOnlyWaitingJobs",
		"Trigger.Queries.FiltersByCreationTime",
		"Trigger.Release.ReleasesOnceUnderContention",
		"Trigger.Release.ReleasesWaitingJobOnce",
		"Trigger.Release.SchedulesFutureDueTime",
	];

	private static readonly string[] GraphCases =
	[
		"Definitions.Removal.SettlesCancelledDependenciesAndBatchCounts",
		"Graph.Batches.CommitsAndProjectsAtomically",
		"Graph.Batches.LoadsExistingGraphWithDependencyMetadata",
		"Graph.Batches.PagesTiedTimestampsWithFilters",
		"Graph.Batches.RollbackInvalidBatchWithoutPartialWrites",
		"Graph.Capability.ResolvesAdvertisedStorage",
		"Graph.Dependencies.AppliesDelayFromParentSettlement",
		"Graph.Dependencies.EvaluatesAlreadyTerminalParents",
		"Graph.Dependencies.FanInWaitsForEveryParent",
		"Graph.Dependencies.ReleasesAndSkipsConditionalBranches",
		"Graph.Dynamic.BufferedSpliceWaitsForEveryAddition",
		"Graph.Dynamic.ConcurrentSplicePreservesOriginalParentAndTrigger",
		"Graph.Dynamic.RejectsInvalidBatchRelationshipsAtomically",
		"Graph.Dynamic.RejectsStaleActiveExecution",
		"Graph.Dynamic.SpliceAndOwnershipAreAtomic",
		"Graph.Dynamic.SplicedWaitersRequireInsertedJobSuccess",
		"Graph.Maintenance.CancelsUnsettledDependencyChains",
		"Graph.Maintenance.PurgesExpiredBatchesWithContinuations",
		"Graph.Maintenance.SerializesBatchPurgeWithRetry",
		"Graph.Maintenance.StandalonePurgePreservesBatchMembers",
		"Graph.Submission.ResolvesQueuesAtomically",
		"Graph.Validation.RejectsCyclesWithoutPartialWrites",
		"Graph.Validation.RejectsDuplicateLogicalEdgesWithoutPartialWrites",
		"Recurring.Materialization.EvaluatesTerminalParentsAtomically",
		"Trigger.Batch.CancelsWaitingBatch",
		"Trigger.Batch.ReleasesEveryMemberTogether",
		"Trigger.Graph.SkipsWhenParentConditionFailsWhileWaiting",
		"Trigger.Graph.WaitsForParentsAndTrigger",
	];

	private static readonly string[] LeaseCases =
	[
		"Definitions.Concurrency.SharesSlotsAndRecoversExpiredLeases",
		"FairQueues.NoisyNeighbors.IgnoresExpiredLeases",
		"Queue.Leases.ReclaimsExpiredLeaseAndRejectsStaleOwner",
	];

	private static readonly string[] ProcessOwnershipCases =
	[
		"FairQueues.NoisyNeighbors.CountsProcessOwnedActiveJobs",
		"Queue.Ownership.RetainsProcessOwnedJobsUntilExplicitCompletion",
	];

	private static readonly string[] QueueInterruptionCases =
	[
		"Queue.Initialization.InterruptsAbandonedExecutionsWithoutResettingLimits",
	];

	private static readonly string[] GraphInterruptionCases =
	[
		"Queue.Initialization.InterruptsLegacyExecution",
	];

	private static readonly string[] ReplicaCases =
	[
		"Replica.Acquisition.AdvancesFairnessOnlyForConfirmedClaims",
		"Replica.Acquisition.ClaimsEachInvocationVersionOnceUnderContention",
		"Replica.Acquisition.ClaimsExactlyTheRequestedDueJobs",
		"Replica.Acquisition.FailedClaimsPreserveConcurrencyFairState",
		"Replica.Acquisition.FailedClaimsPreserveConcurrencyOrderedState",
		"Replica.Acquisition.FailedClaimsPreserveFixedWindowFairState",
		"Replica.Acquisition.FailedClaimsPreserveFixedWindowOrderedState",
		"Replica.Acquisition.FailedClaimsPreserveSlidingWindowFairState",
		"Replica.Acquisition.FailedClaimsPreserveSlidingWindowOrderedState",
		"Replica.Acquisition.FailedClaimsPreserveUnboundedFairState",
		"Replica.Acquisition.FailedClaimsPreserveUnboundedOrderedState",
		"Replica.Acquisition.FailedReclaimsPreserveRenewedLeasesAndExecutionHistory",
		"Replica.Acquisition.IgnoresDuplicateAndMissingRequestedIds",
		"Replica.Acquisition.MirrorsClaimsWhenCancellationArrivesAfterCommit",
		"Replica.Acquisition.PersistsOwnershipLeaseAttemptAndHistory",
		"Replica.Acquisition.PreservesRequestedOrderWhenLimitsAllowOnlyOneClaim",
		"Replica.Acquisition.RestoresPausesAndMirrorsResumeWithoutDurablePolling",
		"Replica.Acquisition.RetainsSlidingHistoryAcrossRecoveryAndPartialClaims",
		"Replica.Acquisition.WaitsUntilEnqueueHasUpdatedBothStores",
		"Replica.Leases.ReclaimsExpiredExecutionAndFencesStaleOwner",
		"Replica.Monitoring.PersistsSingleServerHeartbeatsForRemoteReaders",
		"Replica.Projection.PreservesRecurringAndGraphRecoveryFields",
		"Replica.Recovery.InterruptsOwnershipBeforeLoadingWorkflowSnapshot",
		"Replica.Recovery.RestoresAllPagesAndPreservesSettledProgress",
		"Replica.Recovery.RestoresCronDefinitionsWithTheirSchedules",
	];
}

using Immediate.Jobs.Aspire.Api.Jobs;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Workflows;

public sealed class GameReleaseWorkflow(
	BatchScheduler batches,
	ApproveGameReleaseJob.Scheduler approveRelease,
	BuildGameClientJob.Scheduler buildClient,
	ProvisionGameServicesJob.Scheduler provisionServices,
	TestGameCompatibilityJob.Scheduler testCompatibility,
	SignGameBinariesJob.Scheduler signBinaries,
	MigratePlayerDataJob.Scheduler migratePlayerData,
	LoadTestGameServicesJob.Scheduler loadTestServices,
	CertifyGameClientJob.Scheduler certifyClient,
	CertifyGameServicesJob.Scheduler certifyServices,
	AssembleReleaseCandidateJob.Scheduler assembleCandidate,
	PublishStoreBuildJob.Scheduler publishStoreBuild,
	DeployGameServicesJob.Scheduler deployServices,
	PrewarmDownloadCdnJob.Scheduler prewarmCdn,
	BriefPlayerSupportJob.Scheduler briefSupport,
	OpenReleaseGateJob.Scheduler openReleaseGate,
	AnnounceGameReleaseJob.Scheduler announceRelease,
	EnableMatchmakingJob.Scheduler enableMatchmaking,
	StartLaunchTelemetryJob.Scheduler startTelemetry,
	ConfirmGlobalLaunchJob.Scheduler confirmLaunch
)
{
	public const int JobCount = 19;

	public async ValueTask<BatchHandle> CreateAsync(
		Guid releaseId,
		string title,
		CancellationToken cancellationToken = default
	)
	{
		await using var batch = batches.Begin();

		var payload = new GameReleasePayload(releaseId, title);

		var approved = approveRelease.Enqueue(payload, batch);

		// 1 → 2: start the client and online-service workstreams in parallel.
		var clientBuild = buildClient.ScheduleAfter(
			payload,
			approved
		);
		var serviceProvisioning = provisionServices.ScheduleAfter(
			payload,
			approved
		);

		// Each workstream forms its own 1 → 2 → 1 diamond.
		var compatibility = testCompatibility.ScheduleAfter(
			payload,
			clientBuild
		);
		var signedBinaries = signBinaries.ScheduleAfter(
			payload,
			clientBuild
		);
		var migratedPlayerData = migratePlayerData.ScheduleAfter(
			payload,
			serviceProvisioning
		);
		var loadTestedServices = loadTestServices.ScheduleAfter(
			payload,
			serviceProvisioning
		);

		var certifiedClient = certifyClient.ScheduleAfter(
			payload,
			[compatibility, signedBinaries]
		);
		var certifiedServices = certifyServices.ScheduleAfter(
			payload,
			[migratedPlayerData, loadTestedServices]
		);

		// 2 → 1: both certified workstreams are required for the release candidate.
		var releaseCandidate = assembleCandidate.ScheduleAfter(
			payload,
			[certifiedClient, certifiedServices]
		);

		// 1 → 4 → 1: distribution tasks converge at the release gate.
		var publishedStoreBuild = publishStoreBuild.ScheduleAfter(
			payload,
			releaseCandidate
		);
		var deployedServices = deployServices.ScheduleAfter(
			payload,
			releaseCandidate
		);
		var prewarmedCdn = prewarmCdn.ScheduleAfter(
			payload,
			releaseCandidate
		);
		var briefedSupport = briefSupport.ScheduleAfter(
			payload,
			releaseCandidate
		);

		var releaseGate = openReleaseGate.ScheduleAfter(
			payload,
			[publishedStoreBuild, deployedServices, prewarmedCdn, briefedSupport]
		);

		// 1 → 3 → 1: launch activities run together before final confirmation.
		var announcement = announceRelease.ScheduleAfter(
			payload,
			releaseGate
		);
		var matchmaking = enableMatchmaking.ScheduleAfter(
			payload,
			releaseGate
		);
		var telemetry = startTelemetry.ScheduleAfter(
			payload,
			releaseGate
		);

		_ = confirmLaunch.ScheduleAfter(
			payload,
			[announcement, matchmaking, telemetry]
		);

		return await batch.CommitAsync(cancellationToken);
	}
}

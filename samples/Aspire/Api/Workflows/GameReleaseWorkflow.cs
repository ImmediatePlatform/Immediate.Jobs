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

		var approved = approveRelease.AddToBatch(batch, payload);

		// 1 → 2: start the client and online-service workstreams in parallel.
		var clientBuild = await buildClient.ScheduleAfterAsync(
			approved,
			payload,
			cancellationToken: cancellationToken
		);
		var serviceProvisioning = await provisionServices.ScheduleAfterAsync(
			approved,
			payload,
			cancellationToken: cancellationToken
		);

		// Each workstream forms its own 1 → 2 → 1 diamond.
		var compatibility = await testCompatibility.ScheduleAfterAsync(
			clientBuild,
			payload,
			cancellationToken: cancellationToken
		);
		var signedBinaries = await signBinaries.ScheduleAfterAsync(
			clientBuild,
			payload,
			cancellationToken: cancellationToken
		);
		var migratedPlayerData = await migratePlayerData.ScheduleAfterAsync(
			serviceProvisioning,
			payload,
			cancellationToken: cancellationToken
		);
		var loadTestedServices = await loadTestServices.ScheduleAfterAsync(
			serviceProvisioning,
			payload,
			cancellationToken: cancellationToken
		);

		var certifiedClient = await certifyClient.ScheduleAfterAsync(
			[compatibility, signedBinaries],
			payload,
			cancellationToken: cancellationToken
		);
		var certifiedServices = await certifyServices.ScheduleAfterAsync(
			[migratedPlayerData, loadTestedServices],
			payload,
			cancellationToken: cancellationToken
		);

		// 2 → 1: both certified workstreams are required for the release candidate.
		var releaseCandidate = await assembleCandidate.ScheduleAfterAsync(
			[certifiedClient, certifiedServices],
			payload,
			cancellationToken: cancellationToken
		);

		// 1 → 4 → 1: distribution tasks converge at the release gate.
		var publishedStoreBuild = await publishStoreBuild.ScheduleAfterAsync(
			releaseCandidate,
			payload,
			cancellationToken: cancellationToken
		);
		var deployedServices = await deployServices.ScheduleAfterAsync(
			releaseCandidate,
			payload,
			cancellationToken: cancellationToken
		);
		var prewarmedCdn = await prewarmCdn.ScheduleAfterAsync(
			releaseCandidate,
			payload,
			cancellationToken: cancellationToken
		);
		var briefedSupport = await briefSupport.ScheduleAfterAsync(
			releaseCandidate,
			payload,
			cancellationToken: cancellationToken
		);

		var releaseGate = await openReleaseGate.ScheduleAfterAsync(
			[publishedStoreBuild, deployedServices, prewarmedCdn, briefedSupport],
			payload,
			cancellationToken: cancellationToken
		);

		// 1 → 3 → 1: launch activities run together before final confirmation.
		var announcement = await announceRelease.ScheduleAfterAsync(
			releaseGate,
			payload,
			cancellationToken: cancellationToken
		);
		var matchmaking = await enableMatchmaking.ScheduleAfterAsync(
			releaseGate,
			payload,
			cancellationToken: cancellationToken
		);
		var telemetry = await startTelemetry.ScheduleAfterAsync(
			releaseGate,
			payload,
			cancellationToken: cancellationToken
		);

		_ = await confirmLaunch.ScheduleAfterAsync(
			[announcement, matchmaking, telemetry],
			payload,
			cancellationToken: cancellationToken
		);

		return await batch.CommitAsync(cancellationToken);
	}
}

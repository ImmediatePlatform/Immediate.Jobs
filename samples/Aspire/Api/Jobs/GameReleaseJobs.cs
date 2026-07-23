using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Jobs;

public sealed record GameReleasePayload(Guid ReleaseId, string Title);

[Handler, Job("game-release-approved")]
public sealed partial class ApproveGameReleaseJob(ILogger<ApproveGameReleaseJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "release approved", cancellationToken);
}

[Handler, Job("game-release-build-client")]
public sealed partial class BuildGameClientJob(ILogger<BuildGameClientJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "game client built", cancellationToken);
}

[Handler, Job("game-release-provision-services")]
public sealed partial class ProvisionGameServicesJob(ILogger<ProvisionGameServicesJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "online services provisioned", cancellationToken);
}

[Handler, Job("game-release-test-compatibility")]
public sealed partial class TestGameCompatibilityJob(ILogger<TestGameCompatibilityJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "compatibility tests passed", cancellationToken);
}

[Handler, Job("game-release-sign-binaries")]
public sealed partial class SignGameBinariesJob(ILogger<SignGameBinariesJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "release binaries signed", cancellationToken);
}

[Handler, Job("game-release-migrate-player-data")]
public sealed partial class MigratePlayerDataJob(ILogger<MigratePlayerDataJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "player data migrated", cancellationToken);
}

[Handler, Job("game-release-load-test-services")]
public sealed partial class LoadTestGameServicesJob(ILogger<LoadTestGameServicesJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "online services load-tested", cancellationToken);
}

[Handler, Job("game-release-certify-client")]
public sealed partial class CertifyGameClientJob(ILogger<CertifyGameClientJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "game client certified", cancellationToken);
}

[Handler, Job("game-release-certify-services")]
public sealed partial class CertifyGameServicesJob(ILogger<CertifyGameServicesJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "online services certified", cancellationToken);
}

[Handler, Job("game-release-assemble-candidate")]
public sealed partial class AssembleReleaseCandidateJob(ILogger<AssembleReleaseCandidateJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "release candidate assembled", cancellationToken);
}

[Handler, Job("game-release-publish-store-build")]
public sealed partial class PublishStoreBuildJob(ILogger<PublishStoreBuildJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "store build published", cancellationToken);
}

[Handler, Job("game-release-deploy-services")]
public sealed partial class DeployGameServicesJob(ILogger<DeployGameServicesJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "online services deployed", cancellationToken);
}

[Handler, Job("game-release-prewarm-cdn")]
public sealed partial class PrewarmDownloadCdnJob(ILogger<PrewarmDownloadCdnJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "download CDN prewarmed", cancellationToken);
}

[Handler, Job("game-release-brief-support")]
public sealed partial class BriefPlayerSupportJob(ILogger<BriefPlayerSupportJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "player support briefed", cancellationToken);
}

[Handler, Job("game-release-open-gate")]
public sealed partial class OpenReleaseGateJob(ILogger<OpenReleaseGateJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "global release gate opened", cancellationToken);
}

[Handler, Job("game-release-announce")]
public sealed partial class AnnounceGameReleaseJob(ILogger<AnnounceGameReleaseJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "release announced", cancellationToken);
}

[Handler, Job("game-release-enable-matchmaking")]
public sealed partial class EnableMatchmakingJob(ILogger<EnableMatchmakingJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "matchmaking enabled", cancellationToken);
}

[Handler, Job("game-release-start-telemetry")]
public sealed partial class StartLaunchTelemetryJob(ILogger<StartLaunchTelemetryJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "launch telemetry started", cancellationToken);
}

[Handler, Job("game-release-confirm-launch")]
public sealed partial class ConfirmGlobalLaunchJob(ILogger<ConfirmGlobalLaunchJob> logger)
{
	private ValueTask HandleAsync(GameReleasePayload payload, CancellationToken cancellationToken) =>
		GameReleaseStep.RunAsync(logger, payload, "global launch confirmed", cancellationToken);
}

internal static class GameReleaseStep
{
	public static async ValueTask RunAsync(
		ILogger logger,
		GameReleasePayload payload,
		string step,
		CancellationToken cancellationToken
	)
	{
		logger.LogInformation(
			"Game release {ReleaseId} ({GameTitle}): {WorkflowStep}",
			payload.ReleaseId,
			payload.Title,
			step
		);
		await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
	}
}

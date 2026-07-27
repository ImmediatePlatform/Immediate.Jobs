namespace Immediate.Jobs.Dashboard;

/// <summary>The external telemetry view represented by a dashboard link.</summary>
public enum JobTelemetryLinkKind
{
	/// <summary>A distributed trace or trace search.</summary>
	Trace,
	/// <summary>Structured logs correlated with the job.</summary>
	Logs,
}

/// <summary>Context supplied while constructing an external telemetry link.</summary>
/// <param name="Job">The latest persisted job record.</param>
public sealed record JobTelemetryLinkContext(JobRecord Job);

/// <summary>A link from job details to an external observability system.</summary>
/// <param name="Label">User-facing action label.</param>
/// <param name="Kind">The telemetry view represented by the link.</param>
/// <param name="Url">Absolute or dashboard-relative destination.</param>
public sealed record JobTelemetryLink(string Label, JobTelemetryLinkKind Kind, Uri Url);

internal sealed record JobTelemetryLinkRegistration(
	string Label,
	JobTelemetryLinkKind Kind,
	Func<JobTelemetryLinkContext, Uri?> CreateUrl
);

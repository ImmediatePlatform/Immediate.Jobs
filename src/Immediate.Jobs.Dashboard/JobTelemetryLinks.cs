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
/// <param name="Job">
/// The persisted job record. For an exact-execution link, its attempt, trace ID, span ID, and
/// execution-started compatibility fields represent <see cref="Execution" />.
/// </param>
public sealed record JobTelemetryLinkContext(JobRecord Job)
{
	/// <summary>The exact retained execution being linked, or <see langword="null"/> for a job-level link.</summary>
	public JobExecutionRecord? Execution { get; init; }
}

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

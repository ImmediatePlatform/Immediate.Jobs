namespace Immediate.Jobs.Dashboard;

/// <summary>Configures the Immediate.Jobs dashboard and monitoring API.</summary>
public sealed class ImmediateJobsDashboardOptions
{
	private readonly List<JobTelemetryLinkRegistration> _telemetryLinks = [];

	/// <summary>The interval between server-sent monitoring snapshots.</summary>
	/// <value>The interval between consecutive dashboard updates.</value>
	public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(2);

	internal string? AuthorizationPolicy { get; private set; }
	internal IReadOnlyList<JobTelemetryLinkRegistration> TelemetryLinks => _telemetryLinks;

	/// <summary>Requires the named ASP.NET Core authorization policy on every dashboard endpoint.</summary>
	/// <param name="policy">The registered authorization policy name.</param>
	/// <returns>This options instance.</returns>
	public ImmediateJobsDashboardOptions RequireAuthorization(string policy)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(policy);
		AuthorizationPolicy = policy;
		return this;
	}

	/// <summary>Adds a provider-specific link from job details to an external telemetry system.</summary>
	/// <param name="label">User-facing action label.</param>
	/// <param name="kind">Whether the link opens traces or logs.</param>
	/// <param name="createUrl">
	/// Builds a URL from the latest job record. Return <see langword="null"/> when the link is not
	/// available, such as before an execution trace has been created.
	/// </param>
	/// <returns>This options instance.</returns>
	public ImmediateJobsDashboardOptions AddTelemetryLink(
		string label,
		JobTelemetryLinkKind kind,
		Func<JobTelemetryLinkContext, Uri?> createUrl
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(label);
		ArgumentNullException.ThrowIfNull(createUrl);
		if (!Enum.IsDefined(kind))
			throw new ArgumentOutOfRangeException(nameof(kind));

		_telemetryLinks.Add(new(label, kind, createUrl));
		return this;
	}
}

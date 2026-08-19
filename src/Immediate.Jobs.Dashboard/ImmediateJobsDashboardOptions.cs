using Immediate.Validations.Shared;

namespace Immediate.Jobs.Dashboard;

/// <summary>
///		Configures the Immediate.Jobs dashboard and monitoring API.
/// </summary>
[Validate]
public sealed partial class ImmediateJobsDashboardOptions : IValidationTarget<ImmediateJobsDashboardOptions>
{
	/// <summary>
	///		The interval between server-sent monitoring snapshots.
	/// </summary>
	[GreaterThan(nameof(TimeSpan.Zero))]
	public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(2);

	/// <summary>
	///		Determines whether the dashboard is disabled in non-<c>Development</c> environments.
	/// </summary>
	/// <value>
	///		When <see langword="true" />, dashboard will only be enabled when the current environment
	///		is <c>Development</c>. Otherwise, dashboard will be enabled in all environments.
	///		Default is <see langword="true" />.
	/// </value>
	public bool RestrictToDevelopmentEnvironment { get; set; } = true;

	/// <summary>
	///		Requires the named ASP.NET Core authorization policy on every dashboard endpoint.
	/// </summary>
	/// <value>
	///		The registered authorization policy name.
	///	</value>
	[NotEmpty]
	public string? AuthorizationPolicy { get; set; }

	internal List<JobTelemetryLinkRegistration> TelemetryLinks { get; } = [];
}

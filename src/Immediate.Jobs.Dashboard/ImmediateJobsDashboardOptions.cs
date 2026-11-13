using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http;

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
	///		Ignored when <see cref="AuthorizationPolicy" /> or <see cref="RequestAuthorizer" /> is configured.
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

	/// <summary>
	///		Authorizes each request to the dashboard and monitoring API independently of ASP.NET Core authorization policies.
	/// </summary>
	/// <value>
	///		An asynchronous callback receiving the current <see cref="HttpContext" />. Return <see langword="true" />
	///		to allow access or <see langword="false" /> to return HTTP 403. The default is <see langword="null" />.
	/// </value>
	/// <remarks>
	///		Applies to all dashboard routes, including assets, API mutations, and server-sent events. When configured,
	///		replaces <see cref="RestrictToDevelopmentEnvironment" />. If <see cref="AuthorizationPolicy" /> is also
	///		configured, both checks must allow access. Use <see cref="HttpContext.RequestServices" /> for request-scoped
	///		services and <see cref="HttpContext.RequestAborted" /> for cancellation.
	/// </remarks>
	public Func<HttpContext, ValueTask<bool>>? RequestAuthorizer { get; set; }

	internal List<JobTelemetryLinkRegistration> TelemetryLinks { get; } = [];
}

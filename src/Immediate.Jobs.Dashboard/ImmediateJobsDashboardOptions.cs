namespace Immediate.Jobs.Dashboard;

/// <summary>Configures the Immediate.Jobs dashboard and monitoring API.</summary>
public sealed class ImmediateJobsDashboardOptions
{
	/// <summary>The interval between server-sent monitoring snapshots.</summary>
	public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(2);

	internal string? AuthorizationPolicy { get; private set; }

	/// <summary>Requires the named ASP.NET Core authorization policy on every dashboard endpoint.</summary>
	/// <param name="policy">The registered authorization policy name.</param>
	/// <returns>This options instance.</returns>
	public ImmediateJobsDashboardOptions RequireAuthorization(string policy)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(policy);
		AuthorizationPolicy = policy;
		return this;
	}
}

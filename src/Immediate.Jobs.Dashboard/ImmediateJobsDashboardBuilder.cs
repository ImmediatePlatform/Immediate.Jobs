using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.Dashboard;

/// <summary>
/// 	The fluent registration result returned by <see cref="ImmediateJobsDashboardServiceCollectionExtensions.AddImmediateJobsDashboard(IImmediateJobsBuilder)"/>.
/// </summary>
public interface IImmediateJobsDashboardBuilder : IImmediateJobsBuilder
{
	/// <summary>
	///		Provides an extension point to configure the options using a user provided configuration method.
	/// </summary>
	/// <param name="configureDashboard">
	///		The configuration method used to set the options.
	///	</param>
	/// <returns>
	/// 	The supplied builder.
	/// </returns>
	IImmediateJobsDashboardBuilder ConfigureDashboard(Action<OptionsBuilder<ImmediateJobsDashboardOptions>> configureDashboard);

	/// <summary>
	///	    Adds a provider-specific link from job details to an external telemetry system.
	///	</summary>
	/// <param name="label">
	///	    User-facing action label.
	///	</param>
	/// <param name="kind">
	///	    Whether the link opens traces or logs.
	///	</param>
	/// <param name="createUrl">
	///	    Builds a URL from the job and optional exact execution. Exact-execution requests scope the job's attempt,
	///     trace ID, span ID, and execution-started compatibility fields to that execution. Return <see
	///     langword="null"/> when the link is not available, such as before a trace has been created.
	/// </param>
	/// <returns>
	///		This options instance.
	/// </returns>
	IImmediateJobsDashboardBuilder AddTelemetryLink(
		string label,
		JobTelemetryLinkKind kind,
		Func<JobTelemetryLinkContext, Uri?> createUrl
	);
}

internal sealed class ImmediateJobsDashboardBuilder(IImmediateJobsBuilder builder, OptionsBuilder<ImmediateJobsDashboardOptions> optionsBuilder) : IImmediateJobsDashboardBuilder
{
	public IImmediateJobsDashboardBuilder AddTelemetryLink(
		string label,
		JobTelemetryLinkKind kind,
		Func<JobTelemetryLinkContext, Uri?> createUrl
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(label);

		if (!Enum.IsDefined(kind))
			throw new ArgumentOutOfRangeException(nameof(kind));

		ArgumentNullException.ThrowIfNull(createUrl);

		optionsBuilder.Configure(o => o.TelemetryLinks.Add(new(label, kind, createUrl)));
		return this;
	}

	public IImmediateJobsDashboardBuilder ConfigureDashboard(Action<OptionsBuilder<ImmediateJobsDashboardOptions>> configureDashboard)
	{
		configureDashboard(optionsBuilder);
		return this;
	}

	public IServiceCollection Services => builder.Services;

	public IImmediateJobsBuilder AddHealthCheck(string name = "immediate-jobs", HealthStatus? failureStatus = null, IEnumerable<string>? tags = null) =>
		builder.AddHealthCheck(name, failureStatus, tags);

	public IImmediateJobsBuilder ConfigureWorkers(Action<ImmediateJobsOptions> configureJobs) =>
		builder.ConfigureWorkers(configureJobs);

	public IImmediateJobsBuilder ConfigureWorkers(Action<OptionsBuilder<ImmediateJobsOptions>> configureJobs) =>
		builder.ConfigureWorkers(configureJobs);

	public IImmediateJobsBuilder ConfigureStorage(Action<IImmediateJobsStorageBuilder> configure) =>
		builder.ConfigureStorage(configure);

	public IImmediateJobsBuilder DisableWorkers() =>
		builder.DisableWorkers();

	public IImmediateJobsBuilder UseFairQueues() =>
		builder.UseFairQueues();

	public IImmediateJobsBuilder UseFairQueues(Action<OptionsBuilder<FairQueueOptions>> configureFairQueues) =>
		builder.UseFairQueues(configureFairQueues);

	IImmediateJobsBuilder IImmediateJobsBuilder.UseIdGenerator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TGenerator>() =>
		builder.UseIdGenerator<TGenerator>();
}

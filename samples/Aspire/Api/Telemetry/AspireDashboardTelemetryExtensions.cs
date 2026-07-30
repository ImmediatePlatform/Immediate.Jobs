using Immediate.Jobs.Dashboard;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Telemetry;

internal static class AspireDashboardTelemetryExtensions
{
	public static ImmediateJobsDashboardOptions AddAspireTelemetryLinks(
		this ImmediateJobsDashboardOptions options,
		Uri dashboardUrl
	)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(dashboardUrl);
		if (!dashboardUrl.IsAbsoluteUri ||
			(!string.Equals(dashboardUrl.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && !string.Equals(dashboardUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
		{
			throw new ArgumentException("The Aspire dashboard URL must use HTTP or HTTPS.", nameof(dashboardUrl));
		}

		var baseUrl = new Uri(dashboardUrl.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
		return options
			.AddTelemetryLink(
				"Aspire trace",
				JobTelemetryLinkKind.Trace,
				context => CreateTraceUrl(baseUrl, context.Job)
			)
			.AddTelemetryLink(
				"Aspire attempt logs",
				JobTelemetryLinkKind.Logs,
				context => CreateLogsUrl(baseUrl, context.Job)
			);
	}

	private static Uri? CreateTraceUrl(Uri baseUrl, JobRecord job)
	{
		if (job.ExecutionTraceId is not { } traceId)
			return null;

		var path = $"traces/detail/{Uri.EscapeDataString(traceId)}";
		return job.ExecutionSpanId is { } spanId
			? new(baseUrl, $"{path}?spanId={Uri.EscapeDataString(spanId)}")
			: new(baseUrl, path);
	}

	private static Uri? CreateLogsUrl(Uri baseUrl, JobRecord job)
	{
		if (job.ExecutionTraceId is not { } traceId)
			return null;

		var path = $"structuredlogs?traceId={Uri.EscapeDataString(traceId)}";
		return job.ExecutionSpanId is { } spanId
			? new(baseUrl, $"{path}&spanId={Uri.EscapeDataString(spanId)}")
			: new(baseUrl, path);
	}
}

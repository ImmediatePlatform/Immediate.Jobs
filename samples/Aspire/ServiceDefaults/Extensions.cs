using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

#pragma warning disable IDE0130 // Aspire service defaults are exposed through the standard hosting namespace.
namespace Microsoft.Extensions.Hosting;
#pragma warning restore IDE0130

public static class ServiceDefaultsExtensions
{
	public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
		where TBuilder : IHostApplicationBuilder
	{
		builder.ConfigureOpenTelemetry();

		_ = builder.Services.AddHealthChecks()
			.AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);

		return builder;
	}

	public static WebApplication MapDefaultEndpoints(this WebApplication app)
	{
		_ = app.MapHealthChecks("/health");
		_ = app.MapHealthChecks("/alive", new HealthCheckOptions
		{
			Predicate = static check => check.Tags.Contains("live"),
		});

		return app;
	}

	private static void ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
		where TBuilder : IHostApplicationBuilder
	{
		var hasOtlpEndpoint = !string.IsNullOrWhiteSpace(
			builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
		);

		_ = builder.Logging.AddOpenTelemetry(logging =>
		{
			logging.IncludeFormattedMessage = true;
			logging.IncludeScopes = true;
		});

		var openTelemetry = builder.Services.AddOpenTelemetry()
			.WithMetrics(metrics => metrics
				.AddAspNetCoreInstrumentation()
				.AddHttpClientInstrumentation()
				.AddRuntimeInstrumentation()
				.AddMeter("Immediate.Jobs", "Npgsql"))
			.WithTracing(tracing => tracing
				.AddSource(builder.Environment.ApplicationName, "Immediate.Jobs", "Npgsql")
				.AddAspNetCoreInstrumentation()
				.AddHttpClientInstrumentation());

		if (hasOtlpEndpoint)
			_ = openTelemetry.UseOtlpExporter();
	}
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

public static class Extensions
{
	public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
		where TBuilder : IHostApplicationBuilder
	{
		builder.ConfigureOpenTelemetry();

		builder.Services.AddHealthChecks()
			.AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);

		return builder;
	}

	public static WebApplication MapDefaultEndpoints(this WebApplication app)
	{
		app.MapHealthChecks("/health");
		app.MapHealthChecks("/alive", new HealthCheckOptions
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

		builder.Logging.AddOpenTelemetry(logging =>
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
			openTelemetry.UseOtlpExporter();
	}
}

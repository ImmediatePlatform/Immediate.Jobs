# Immediate.Jobs.Dashboard

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.Dashboard.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.Dashboard/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/dashboard-and-monitoring)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

An embedded monitoring dashboard and stable HTTP API for
[Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/). The package serves an embedded SPA plus
Immediate.Apis-generated JSON and Server-Sent Events endpoints.

## Installation

```console
dotnet add package Immediate.Jobs --prerelease
dotnet add package Immediate.Jobs.Dashboard --prerelease
```

## Registration and mapping

Register the dashboard's generated handlers before building the app, then map it:

```csharp
using Immediate.Jobs.Dashboard;

builder.Services.AddImmediateJobsDashboard(options =>
{
	_ = options.RequireAuthorization("operations");
});

var app = builder.Build();
app.MapImmediateJobsDashboard("/jobs");
```

Without an authorization policy, every dashboard endpoint is allowed only in the `Development` environment and returns
403 elsewhere. For a trusted custom development environment, explicitly disable this restriction when mapping:

```csharp
app.MapImmediateJobsDashboard("/jobs", options =>
	_ = options.AllowInAnyEnvironment()
);
```

Treat the dashboard as an administrative surface: it exposes payloads, errors, identifiers, and mutations. Prefer
`RequireAuthorization(...)` whenever the dashboard is exposed outside a trusted environment. A named policy applies to
UI assets and APIs together and remains authoritative if `AllowInAnyEnvironment()` is also configured.

Immediate.Validations returns `application/problem+json` for invalid route and paging inputs.

## Features

The dashboard includes:

- filtered, server-paged job search;
- job details and retained execution attempts;
- recurring schedule actions;
- retry, cancellation, and atomic batch deletion;
- batch progress and a live dependency-graph viewer;
- live updates over Server-Sent Events; and
- application-defined links to traces and logs.

Job search and filters are paged on the server in groups of 50. Batch members link back to their workflow.

## Telemetry links

Telemetry destinations are application-defined because Aspire, Jaeger, Grafana, Seq, Azure Monitor, and other systems
use different query URLs. Register callbacks for execution traces, execution logs, or a stable job-level query across
all retries:

```csharp
var traceExplorer = new Uri("https://traces.example/");
var logExplorer = new Uri("https://logs.example/");

builder.Services.AddImmediateJobsDashboard(options =>
{
	_ = options.RequireAuthorization("operations");

	_ = options.AddTelemetryLink(
		"View execution trace",
		JobTelemetryLinkKind.Trace,
		context => context.Execution?.ExecutionTraceId is { } traceId
			? new(traceExplorer, $"trace/{traceId}")
			: null);

	_ = options.AddTelemetryLink(
		"View execution logs",
		JobTelemetryLinkKind.Logs,
		context => context.Execution is { } execution
			? new(logExplorer,
				$"search?jobId={Uri.EscapeDataString(context.Job.Id)}&attempt={execution.Attempt}")
			: null);

	_ = options.AddTelemetryLink(
		"View all retry logs",
		JobTelemetryLinkKind.Logs,
		context => context.Execution is null
			? new(logExplorer, $"search?jobId={Uri.EscapeDataString(context.Job.Id)}")
			: null);
});
```

Each execution attempt creates a distinct `Activity` linked to the enqueue context. Every acquired execution is
retained with its outcome, worker, timing, trace and span identifiers, and full failure text until its owning job or
batch is deleted.

The job-detail timeline is newest first and supplies the exact execution to telemetry-link callbacks. Job-level
callbacks receive `Execution = null`. For compatibility, exact-execution callbacks also expose the selected attempt,
trace ID, span ID, and start time through the corresponding latest-execution fields on `context.Job`.

`AddTelemetryLink` callbacks may return `null` when a destination does not apply and may return HTTP(S) or
dashboard-relative URLs.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Dashboard and monitoring documentation](https://immediateplatform.dev/docs/Immediate.Jobs/dashboard-and-monitoring)
- [Aspire sample](https://github.com/ImmediatePlatform/Immediate.Jobs/tree/main/samples/Aspire)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)

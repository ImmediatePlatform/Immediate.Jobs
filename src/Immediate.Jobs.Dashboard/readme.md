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

Add the dashboard to the generated jobs builder before building the app, then map it:

```csharp
using Immediate.Jobs.Dashboard;

builder.Services.AddMyAppHandlers();
builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage.UseInMemory())
	.AddImmediateJobsDashboard()
	.ConfigureDashboard(options => options.AuthorizationPolicy = "operations");

var app = builder.Build();
app.MapImmediateJobsDashboard("/jobs");
```

Dashboard options use the .NET options system and are validated when the host starts. Configure them through
`ConfigureDashboard`; `MapImmediateJobsDashboard` only selects the URL path.

Without an authorization policy, every dashboard endpoint is allowed only in the `Development` environment and returns
403 elsewhere. For a trusted custom development environment, explicitly disable this restriction during registration:

```csharp
builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage.UseInMemory())
	.AddImmediateJobsDashboard()
	.ConfigureDashboard(options => options.RestrictToDevelopmentEnvironment = false);

var app = builder.Build();
app.MapImmediateJobsDashboard("/jobs");
```

Treat the dashboard as an administrative surface: it exposes payloads, errors, identifiers, and mutations. Prefer
setting `AuthorizationPolicy` whenever the dashboard is exposed outside a trusted environment. A named policy applies
to UI assets and APIs together and replaces the development-only restriction.

Immediate.Validations returns `application/problem+json` for invalid route and paging inputs.

## Features

The dashboard includes:

- filtered, server-paged job search;
- job details and retained execution attempts;
- recurring schedule actions;
- retry, cancellation, and batch cancellation or deletion;
- batch progress and a live dependency-graph viewer;
- live updates over Server-Sent Events; and
- application-defined links to traces and logs.

Job search and filters are paged on the server in groups of 50. Batch members link back to their workflow.

## Identifier fields

Dashboard JSON uses `jobId` for job records and `batchId` for batch records. Their values remain opaque JSON strings.
The .NET monitoring APIs use `JobHandle` and `BatchHandle` so a job ID cannot be passed to a batch operation by mistake:

```csharp
var job = await monitor.GetJobAsync(
	JobHandle.FromString(jobId),
	cancellationToken);

var batch = await monitor.GetBatchAsync(
	BatchHandle.FromString(batchId),
	cancellationToken);
```

The handle converters keep the HTTP representation string-based. In .NET, first take the handle from the record, then
read its `.JobId` or `.BatchId` string when building a route or an external-system query.

## Telemetry links

Telemetry destinations are application-defined because Aspire, Jaeger, Grafana, Seq, Azure Monitor, and other systems
use different query URLs. Register callbacks for execution traces, execution logs, or a stable job-level query across
all retries:

```csharp
var traceExplorer = new Uri("https://traces.example/");
var logExplorer = new Uri("https://logs.example/");

builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage.UseInMemory())
	.AddImmediateJobsDashboard()
	.ConfigureDashboard(options => options.AuthorizationPolicy = "operations")
	.AddTelemetryLink(
		"View execution trace",
		JobTelemetryLinkKind.Trace,
		context => context.Execution?.ExecutionTraceId is { } traceId
			? new(traceExplorer, $"trace/{traceId}")
			: null)
	.AddTelemetryLink(
		"View execution logs",
		JobTelemetryLinkKind.Logs,
		context =>
		{
			var jobHandle = context.Job.JobId;
			return context.Execution is { } execution
				? new(logExplorer,
					$"search?jobId={Uri.EscapeDataString(jobHandle.JobId)}&attempt={execution.Attempt}")
				: null;
		})
	.AddTelemetryLink(
		"View all retry logs",
		JobTelemetryLinkKind.Logs,
		context =>
		{
			var jobHandle = context.Job.JobId;
			return context.Execution is null
				? new(logExplorer, $"search?jobId={Uri.EscapeDataString(jobHandle.JobId)}")
				: null;
		});
```

Each execution attempt creates a distinct `Activity` linked to the enqueue context. Every acquired execution is
retained with its outcome, worker, timing, trace and span identifiers, and full failure text until its owning job or
batch is deleted.

The job-detail timeline is newest first. Job-level callbacks receive `Execution = null`, which is useful for links that
search by the stable job ID across all retries. Execution-level callbacks receive the exact retained attempt, including
its attempt number, trace and span IDs, and timing.

`AddTelemetryLink` callbacks may return `null` when a destination does not apply and may return HTTP(S) or
dashboard-relative URLs.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Dashboard and monitoring documentation](https://immediateplatform.dev/docs/Immediate.Jobs/dashboard-and-monitoring)
- [Aspire sample](https://github.com/ImmediatePlatform/Immediate.Jobs/tree/main/samples/Aspire)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)

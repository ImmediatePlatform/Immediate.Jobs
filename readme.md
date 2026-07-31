# Immediate.Jobs

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs/)
[![GitHub release](https://img.shields.io/github/release/ImmediatePlatform/Immediate.Jobs.svg)](https://GitHub.com/ImmediatePlatform/Immediate.Jobs/releases/)
[![GitHub license](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/master/license.txt) 
[![GitHub issues](https://img.shields.io/github/issues/ImmediatePlatform/Immediate.Jobs.svg)](https://GitHub.com/ImmediatePlatform/Immediate.Jobs/issues/) 
[![GitHub issues-closed](https://img.shields.io/github/issues-closed/ImmediatePlatform/Immediate.Jobs.svg)](https://GitHub.com/ImmediatePlatform/Immediate.Jobs/issues?q=is%3Aissue+is%3Aclosed) 
[![GitHub Actions](https://github.com/ImmediatePlatform/Immediate.Jobs/actions/workflows/build.yml/badge.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/actions)
[![Coverage Status](https://coveralls.io/repos/github/ImmediatePlatform/Immediate.Jobs/badge.svg)](https://coveralls.io/github/ImmediatePlatform/Immediate.Jobs)
[![Docs](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)

---

Immediate.Jobs is a reflection-free background job scheduler for .NET 8+ built on Immediate.Handlers. A job is an
`[Handler]` whose request can also be durably enqueued; a Roslyn source generator emits its typed scheduler, payload
metadata, and dependency-injection registrations at compile time.

> Immediate.Jobs provides **at-least-once delivery**. Every handler that performs externally visible work must be
idempotent. The in-memory provider is single-node, non-durable, and intended only for development, tests, and
non-critical work.

## Define and enqueue a job

```csharp
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

[Handler, Job(Name = "send-welcome-email", MaxAttempts = 5, Timeout = "00:02:00")]
public sealed partial class SendWelcomeEmail(IEmailSender sender)
{
	public sealed record Payload(Guid UserId, string Template);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		new(sender.SendAsync(payload.UserId, payload.Template, cancellationToken));
}

public sealed class SignupService(SendWelcomeEmail.Scheduler welcomeEmail)
{
	public ValueTask<JobHandle> EnqueueAsync(Guid userId, CancellationToken cancellationToken) =>
		welcomeEmail.EnqueueAsync(new(userId, "v2"), cancellationToken);
}
```

`Scheduler` is a nested generated type. The worker invokes the generated `SendWelcomeEmail.Handler`, so the same handler and its ordinary Immediate.Handlers behaviors work both inline and in the background. `[Job]` is class-only and is rejected unless the class is also marked `[Handler]`.

`EnqueueAsync`, `ScheduleAsync`, `ScheduleAtAsync`, and `TriggerNowAsync` each return a `JobHandle`. Its `Id` is an opaque
string invocation ID. Consumers must not parse it or depend on its format; storage integrations may
use another string ID scheme.

## Register Jobs and Execution Engine

In your `Program.cs`, add a call to `services.AddXxxJobs()`, where `Xxx` is the application identifier.
By default, this is the short form of the assembly name. For example:
* For a project named `Web`, it will be `services.AddWebJobs()`
* For a project named `Application.Web`, it will be `services.AddApplicationWebJobs()`

However, this name can be overridden using `[assembly: ImmediateAssemblyIdentifier("SomeIdentifier")]`.

> [!NOTE]
> Because Jobs are based on [Immediate.Handlers](https://github.com/ImmediatePlatform/Immediate.Handlers) handlers,
> they must be registered as well, using `services.AddXxxHandlers()`.

## Batches and continuations

Inject `IJobBatchScheduler` to create an atomic group. Generated schedulers inherit strongly typed
batch methods and return handles that can be connected into chains, fan-out branches, and fan-in
joins. Storage receives the entire batch in one transaction; disposing without committing writes
nothing.

```csharp
await using var batch = batches.Begin();

var imported = import.AddToBatch(batch, new(importId));
var indexed = await index.ScheduleAfterAsync(imported, new(importId), cancellationToken: cancellationToken);

var notifyOwner = await notify.ScheduleAfterAsync(indexed, new(importId), cancellationToken: cancellationToken);
var updateMetrics = await metrics.ScheduleAfterAsync(indexed, new(importId), cancellationToken: cancellationToken);

await finalize.ScheduleAfterAsync(
	[notifyOwner, updateMetrics],
	new(importId),
	cancellationToken: cancellationToken
);

BatchHandle committed = await batch.CommitAsync(cancellationToken);
```

`ContinuationTrigger.Success` is the default; `Failure` runs after every parent settles when at
least one failed, and `Complete` runs regardless of outcome. A running batch member can also expand its workflow through its injected
`JobDetails`, with additions buffered until the attempt succeeds so retries do not duplicate work.

## Queues, priority, and concurrency

Define queues as strongly typed marker classes and assign jobs with `UsesQueue<TQueue>`:

```csharp
[QueueDefinition(Name = "transactional-email", Priority = 100, Concurrency = 2)]
public sealed class TransactionalEmailQueue;

[Handler, Job, UsesQueue<TransactionalEmailQueue>]
public sealed partial class SendWelcomeEmail(IEmailSender sender)
{
	// ...
}
```

Larger priority values are acquired first. Queues at the same priority are considered in round-robin order, while jobs within a queue remain ordered by due time and creation time. `Concurrency` is a per-node limit; zero is unbounded. It composes with `Job.MaxConcurrency` and the global `MaxParallelJobs` limit.

When `Name` is omitted it is derived from the queue type (`TransactionalEmailQueue` becomes `transactional-email-queue`). Jobs without `UsesQueue<TQueue>` use the unbounded priority-zero `default` queue. Queue names are persisted with each invocation: keep an old definition until its nonterminal jobs drain, or migrate those rows before renaming/removing it.

### Fair queues

Fair queues prevent one tenant's backlog from starving quieter tenants in the same queue. Supply a
runtime group id when directly enqueueing or scheduling work, then opt into fair acquisition:

```csharp
await welcomeEmail.EnqueueAsync(
	new(userId, "v2"),
	groupId: tenantId,
	cancellationToken: cancellationToken
);

builder.Services.AddMyAppJobs(options =>
{
	options.UseEntityFrameworkCore<AppDbContext>();
	options.UseDistributed();
	options.UseFairQueues();
});
```

Fair queues are not FIFO and do not serialize a group. They rotate eligible work across groups and
deprioritize a group only when its non-expired in-flight share exceeds the configured threshold.
Null, empty, or whitespace group ids are ungrouped and retain the existing due-time order. Group ids
should identify reusable tenants, not individual jobs; use `null` for ordinary ungrouped work.

In-memory, EF Core, and LinqToDB support fair acquisition. Redis persists the group id but rejects fair acquisition in
this release. EF applications must migrate the nullable `GroupId` column, `(QueueName, State, GroupId)` index, and
`immediate_fair_queue_groups` table. LinqToDB schema bootstrap creates them for new databases; existing databases need
the equivalent additive upgrade. See [Fair queues](docs/fair-queues.md) for the algorithm, schema, tradeoffs, and
provider details.

## Recurring work

Code-defined schedules use five-field cron or six-field cron with seconds. See the [Cronos usage guide](https://github.com/HangfireIO/Cronos#Usage) for the supported syntax:

```csharp
[Handler, Job(Cron = "0 */5 * * * *", TimeZone = "Europe/Vienna")]
public sealed partial class CleanupSessionsJob(AppDbContext db)
{
	private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken) =>
		new(db.DeleteExpiredSessions(cancellationToken));
}
```

Inject `CleanupSessionsJob.Scheduler` to trigger the code-defined job immediately:

```csharp
await scheduler.TriggerNowAsync(cancellationToken);
```

Schedulers for jobs with a compile-time `Cron` do not expose dynamic schedule mutation. To manage named schedules at runtime, define a separate payloadless job without `Cron`:

```csharp
[Handler, Job(Name = "tenant-cleanup")]
public sealed partial class TenantCleanupJob(AppDbContext db)
{
	private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken) =>
		new(db.DeleteExpiredSessions(cancellationToken));
}

await tenantCleanupScheduler.AddOrUpdateRecurringAsync("tenant-42-cleanup", "0 0 3 * * *", "UTC", cancellationToken);
await tenantCleanupScheduler.RemoveRecurringAsync("tenant-42-cleanup", cancellationToken);
```

## Immediate.Handlers behaviors for jobs

Background dispatch calls the generated Immediate.Handlers handler, so jobs use the same behavior pipeline as inline
requests. A job request does not need a jobs-specific base type or interface. Implement the optional `IJobRequest`
capability only when the handler or a behavior needs execution metadata; the worker then populates its non-persisted
`JobDetails` immediately before entering the pipeline.

```csharp
public sealed record Payload(Guid UserId, string Template) : IJobRequest
{
	public JobDetails? JobDetails { get; set; }
}
```

```csharp
[assembly: Behaviors(typeof(JobLoggingBehavior<,>))]

public sealed class JobLoggingBehavior<TRequest, TResponse>(ILogger<JobLoggingBehavior<TRequest, TResponse>> logger)
	: Behavior<TRequest, TResponse>
	where TRequest : IJobRequest
{
	public override async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken)
	{
		var details = request.JobDetails ?? throw new InvalidOperationException("Job details are unavailable.");
		logger.LogInformation("Starting {JobName} attempt {Attempt}", details.JobName, details.Attempt);
		return await Next(request, cancellationToken);
	}
}
```

The `IJobRequest` constraint keeps this global behavior out of ordinary handlers and jobs that have not opted into
execution metadata. Use Immediate.Handlers `[Behaviors(...)]` directly on a job to replace assembly behaviors, or put
`[Behaviors(...)]` on a reusable custom attribute for a named job pipeline. Handler behaviors execute inside the retry
boundary.

## Propagating scoped context

Ambient or request-scoped context can be captured and restored to be available ambiently during job execution. Dedicated
context extractors are used to provide this capability. An extractor must derive from `JobContextExtractor<TContext>`
and implement the necessary `abstract` methods. The context type specified as `TContext` will be serialized as part of
the job creation process, and deserialized and provided back to the extractor to be restored during the job execution.
The context value is serialized with generated metadata, so it remains trimming- and Native AOT-safe.

```csharp
// This is the durable, serializable value stored with the job.
public sealed record UsageContextSnapshot(Guid UserId, string TenantId);

// This is the scoped application service populated by the request and injected through DI.
public sealed class UsageContext
{
	public UsageContextSnapshot? Value { get; set; }
}

public sealed class UsageContextExtractor(UsageContext usage)
	: JobContextExtractor<UsageContextSnapshot>
{
	public override string Key => "usage"; // stable across extractor type renames

	public override UsageContextSnapshot? Capture() => usage.Value;

	public override void Restore(UsageContextSnapshot context) => usage.Value = context;
}

[Handler, Job, UsesJobContext<UsageContextExtractor>]
public sealed partial class AuditUsageJob(UsageContext usage)
{
	// usage.Value contains the enqueueing scope's snapshot when this job runs.
}
```

Register the application-owned holder as scoped: `builder.Services.AddScoped<UsageContext>()`, or using
[Immediate.Injections](https://github.com/ImmediatePlatform/Immediate.Injections), by applying `[RegisterScoped]` to the
class. The generated job registrations add `UsageContextExtractor` as scoped automatically. At enqueue, the extractor
reads the caller's `UsageContext`; at execution, it writes the deserialized snapshot into the new job scope's
`UsageContext` before the job and its behaviors are resolved. The snapshot itself is persisted data passed to `Restore`,
not a service resolved from DI.

For a family of jobs, put one or more extractor markers on a reusable attribute:

```csharp
[UsesJobContext<UsageContextExtractor>]
[UsesJobContext<CorrelationContextExtractor>]
public sealed class WebJobAttribute : Attribute;

[Handler, Job, WebJob]
public sealed partial class SendInvoiceJob
{
	// Captures and restores both contexts.
}
```

Extractor keys identify persisted envelope slices and must be unique for a job. Return `null` from
`Capture` when there is no context to persist, as is typical when recurring work is
materialized outside a request.

## Storage providers

- `Immediate.Jobs` includes the development-only in-memory provider, the memory-primary durable single-server topology, and the channel-backed worker pool.
- `Immediate.Jobs.EntityFrameworkCore` is the provider-neutral EF Core adapter, validated with PostgreSQL, SQLite, and SQL Server.
- `Immediate.Jobs.LinqToDB` is the provider-neutral LinqToDB adapter, validated with PostgreSQL, SQLite, and SQL Server.
- `Immediate.Jobs.Redis` is the distributed queue + recurring adapter. It does not implement batches
  or continuations; those features require one of the graph-capable SQL providers.

All providers implement `IJobStorage`. Single-server mode restores unfinished jobs and recurring schedules into memory when the process starts. Distributed mode uses provider leases; if a process dies, its lease expires and another node can acquire the invocation. Redis always selects distributed mode because the single-server durable-replica topology requires all storage capabilities.

### Redis configuration

Pass either a StackExchange.Redis configuration string or an application-owned
`IConnectionMultiplexer`. `UseRedis` selects distributed mode automatically:

```csharp
builder.Services.AddMyAppJobs(options =>
	options.UseRedis("localhost:6379", redis =>
	{
		redis.Database = 1;
		redis.KeyPrefix = "billing-jobs";
	}));
```

The prefix isolates applications and is also used as the Redis Cluster hash tag, keeping every
atomic Lua transition in one slot. The provider owns connections it creates from a configuration
string; it does not dispose an `IConnectionMultiplexer` supplied by the application. Terminal job
history is tracked in completion-time sorted indexes and removed by the normal
`SucceededRetention` / `FailedRetention` purge loop.

### EF Core configuration

Register an `IDbContextFactory<TContext>`, select the database through its normal EF provider, and
include the jobs model in the application context:

```csharp
builder.Services.AddDbContextFactory<AppDbContext>(db =>
	db.UseNpgsql(connectionString));       // PostgreSQL
// db.UseSqlite(connectionString);       // SQLite
// db.UseSqlServer(connectionString);    // SQL Server

builder.Services.AddMyAppJobs(options =>
	options.UseEntityFrameworkCore<AppDbContext>());

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
	modelBuilder.AddImmediateJobs(schema: "background"); // omit the schema for SQLite
}
```

The application owns EF migrations. Add a migration after calling `AddMyAppJobs`, or use
`EnsureCreatedAsync` only for disposable development/test databases. The adapter does not reference
Npgsql, SQLite, or SQL Server provider packages.

### LinqToDB configuration

Configure and reuse an immutable `DataOptions`, then pass it to both bootstrap and registration:

```csharp
var dataOptions = new DataOptions().UsePostgreSQL(connectionString);
// new DataOptions().UseSQLite(connectionString);
// new DataOptions().UseSqlServer(connectionString);

await dataOptions.CreateImmediateJobsSchemaAsync(
	schema: "background", // must be null for SQLite
	cancellationToken);

builder.Services.AddMyAppJobs(options =>
	options.UseLinqToDB(dataOptions, schema: "background"));
```

`CreateImmediateJobsSchemaAsync` is an explicit, idempotent bootstrap helper for a fresh database.
It is never called by `InitializeAsync` and is not a production migration system. Applications own
the matching ADO.NET driver (`Npgsql`, `Microsoft.Data.Sqlite`, or `Microsoft.Data.SqlClient`); the
adapter package deliberately carries none of them. Named schemas are supported on PostgreSQL and
SQL Server. SQLite has no server schemas and is normally embedded/file-backed, including in the
storage conformance tests.

Upgrading either relational adapter for retained execution history requires the
`immediate_job_executions` table. Apply the corresponding application migration before deploying the
new binaries. During a mixed-version rollout, history remains best effort until all scheduler nodes
have been upgraded.

Queue-aware dispatch changes the provider acquisition seam to `AcquireDueJobsAsync(JobAcquisitionRequest, ...)`. Custom providers must honor the request's queue order, queue capacities, and per-job capacities when upgrading.

## Monitoring API

Use `IJobBatchMonitor` and `IJobMonitor` for status, member, graph, and job reads. See
[Batches & Continuations](docs/batches-and-continuations.md) for the complete API and semantics.

## Dashboard and Monitoring Web API

Reference `Immediate.Jobs.Dashboard`, register its generated handlers before building the app, then map it:

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

var app = builder.Build();
app.MapImmediateJobsDashboard("/jobs");
```

The package serves an embedded SPA plus Immediate.Apis-generated JSON and Server-Sent Events endpoints.
Immediate.Validations returns `application/problem+json` for invalid route and paging inputs.
Without an authorization policy, dashboard access is allowed only in the `Development` environment.
The dashboard includes batch progress and a live dependency-graph viewer alongside filtered jobs,
recurring schedule actions, retry, cancellation, and atomic batch deletion. Job search and filters
are paged on the server in groups of 50; batch members show a link to their workflow.

Telemetry destinations are application-defined because Aspire, Jaeger, Grafana, Seq, Azure Monitor,
and other systems use different query URLs. Each execution attempt creates a distinct `Activity`
linked to the enqueue context. Every acquired execution is retained with its outcome, worker, timing,
trace/span identifiers, and full failure text until its owning job or batch is deleted. The job-detail
timeline is newest first and supplies the exact execution to telemetry-link callbacks; job-level
callbacks receive `Execution = null`, which supports a stable-job-ID log query across every retry.
For compatibility, exact-execution callbacks also see that execution's attempt, trace ID, span ID,
and start time in the corresponding `context.Job` latest-execution fields.
`JobRecord.ExecutionTraceId`, `ExecutionSpanId`, and `ExecutionStartedAt` remain the latest-execution
compatibility projection. `AddTelemetryLink` may return `null` when a destination does not apply, and
accepts HTTP(S) or dashboard-relative URLs.

## Testing

`Immediate.Jobs.Testing` provides `JobTestHarness`, a fake clock, advance-and-drain helpers, capture-only typed schedulers, enqueue assertions, and a single-job handler-pipeline runner. Delayed work, scheduled occurrences, timeout, and backoff tests do not need wall-clock sleeps.

## Diagnostics

Compile-time diagnostics:

| ID | Meaning |
|---|---|
| `IJOB0001` | `[Job]` class is not also an Immediate `[Handler]` |
| `IJOB0002` | Duplicate persisted job name |
| `IJOB0003` | Duplicate persisted queue name |
| `IJOB0004` | `NodaTime` is referenced without `Immediate.Jobs.NodaTime` |
| `IJOB0005` | Invalid retry/concurrency/timeout configuration |
| `IJOB0006` | A cron job declares a payload |
| `IJOB0007` | Invalid cron expression or time zone |
| `IJOB0008` | No usable job name; rename the class or set `Name` |
| `IJOB0009` | `UsesQueue<T>` targets a type without `QueueDefinition` |
| `IJOB0010` | A job class is also marked as a queue definition |
| `IJOB0011` | Invalid queue name or concurrency configuration |
| `IJOB0012` | A job handler has a return value |
| `IJOB0013` | Unsupported payload member or type |
| `IJOB0014` | Unsupported context member or type |
| `IJOB0015` | `AddToBatchAsync(JobDetails, ..., Detached)` is contradictory |

Invalid Immediate.Jobs runtime operations and states throw `ImmediateJobException`. Invalid method
arguments, cron expressions, serialized data, and missing records retain their standard exception types.

## Observability

Executions emit activities from `ActivitySource` `Immediate.Jobs`, metrics from `Meter` `Immediate.Jobs`, structured logs scoped by job name/id/attempt, and scheduler/storage health checks. The metrics include enqueue/success/failure/retry counters, duration histograms, queue depth, and active workers.

The [Aspire sample](samples/Aspire/readme.md) runs the EF Core provider against an Aspire-managed PostgreSQL container and sends application logs, traces, metrics, and health status to the Aspire dashboard. It also exposes the Immediate.Jobs dashboard for job-specific history and operations.

## Benchmarks

The repository includes BenchmarkDotNet comparisons with TickerQ, Hangfire MemoryStorage, and Quartz.NET. In addition to enqueue, direct dispatch, and startup, the suite covers concurrent throughput, cron expressions, delegate invocation, job creation, serialization, and startup registration. These are microbenchmarks of deliberately different framework APIs—not end-to-end durability or worker-latency measurements—so run them on the deployment target before drawing conclusions.

### Results

The tables below are the historical `ShortRun` results from 21 July 2026: BenchmarkDotNet 0.15.8, .NET 8.0.22 Arm64 RyuJIT, Apple M3 Pro with 12 cores, macOS 26.5. Each result uses one launch, three warmup iterations, and three measurement iterations. Ratios use Immediate.Jobs as the baseline. The expanded TickerQ suite targets .NET 10 and does not yet have checked-in results.

#### EnqueueAsync

| Framework | Mean | Ratio | Allocated | Allocation ratio |
|---|---:|---:|---:|---:|
| Immediate.Jobs | 3.796 μs | 1.00 | 5.07 KB | 1.00 |
| Hangfire | 16.122 μs | 4.25 | 14.49 KB | 2.86 |
| Quartz.NET | 17.288 μs | 4.56 | 6.34 KB | 1.25 |

#### Direct dispatch

| Framework | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Immediate.Jobs | 0.9994 ns | 1.00 | 0 B |
| Hangfire | 28.0701 ns | 28.09 | 32 B |
| Quartz.NET | 0.0521 ns | 0.05 | 0 B |

The Immediate.Jobs and Quartz.NET dispatch operations are effectively below the benchmark's reliable measurement floor. Treat their sub-nanosecond values as "no measurable dispatch overhead" rather than literal timing precision.

#### Scheduler construction

| Framework | Mean | Ratio | Allocated | Allocation ratio |
|---|---:|---:|---:|---:|
| Immediate.Jobs | 393.60 ns | 1.00 | 649 B | 1.00 |
| Hangfire | 7,765.75 ns | 19.77 | 3,104 B | 4.78 |
| Quartz.NET | 11.67 ns | 0.03 | 136 B | 0.21 |

The complete generated reports are available for [enqueue](BenchmarkDotNet.Artifacts/results/Immediate.Jobs.Benchmarks.EnqueueBenchmarks-report-github.md), [direct dispatch](BenchmarkDotNet.Artifacts/results/Immediate.Jobs.Benchmarks.DispatchBenchmarks-report-github.md), and [scheduler construction](BenchmarkDotNet.Artifacts/results/Immediate.Jobs.Benchmarks.StartupBenchmarks-report-github.md).

Run the complete suite with:

```console
dotnet run --project benchmarks/Immediate.Jobs.Benchmarks -c Release -- --filter '*'
```

## Delivery and retention defaults

- Lease: 30 seconds, renewed while active.
- Attempts: 3 total, exponential backoff with jitter from 5 seconds.
- Successful history: 24 hours.
- Failed history: 7 days.
- Execution history: the same lifetime as its owning job or batch.
- Graceful worker drain: 30 seconds.

These values are configurable globally or, where applicable, on `[Job]`.

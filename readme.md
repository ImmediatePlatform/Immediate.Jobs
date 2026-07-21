# Immediate.Jobs

Immediate.Jobs is a reflection-free background job scheduler for .NET 8+ built on Immediate.Handlers. A job is an `[Handler]` whose request can also be durably enqueued; a Roslyn source generator emits its typed scheduler, payload metadata, and dependency-injection registrations at compile time.

> Immediate.Jobs provides **at-least-once delivery**. Every handler that performs externally visible work must be idempotent. The in-memory provider is single-node, non-durable, and intended only for development, tests, and non-critical work.

## Define and enqueue a job

```csharp
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

[Handler, Job("send-welcome-email", MaxAttempts = 5, Timeout = "00:02:00")]
public sealed partial class SendWelcomeEmail(IEmailSender sender)
{
	public sealed record Payload(Guid UserId, string Template);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		new(sender.SendAsync(payload.UserId, payload.Template, cancellationToken));
}

public sealed class SignupService(SendWelcomeEmail.Scheduler welcomeEmail)
{
	public ValueTask<string> Enqueue(Guid userId, CancellationToken cancellationToken) =>
		welcomeEmail.Enqueue(new(userId, "v2"), cancellationToken);
}
```

`Scheduler` and `IScheduler` are nested generated types. The worker invokes the generated `SendWelcomeEmail.Handler`, so the same handler and its ordinary Immediate.Handlers behaviors work both inline and in the background. `[Job]` is class-only and is rejected unless the class is also marked `[Handler]`.

Generated schedulers are scoped services. Resolve or inject them from a request or other DI scope so
enqueue-time context extractors can read the same scoped state as the caller. A singleton consumer,
such as an `IHostedService`, must create a scope for each unit of work rather than inject a generated
scheduler directly:

```csharp
public sealed class ImportWorker(IServiceScopeFactory scopeFactory)
{
	public async ValueTask Enqueue(Guid importId, CancellationToken cancellationToken)
	{
		await using var scope = scopeFactory.CreateAsyncScope();
		var scheduler = scope.ServiceProvider.GetRequiredService<ImportJob.Scheduler>();
		await scheduler.Enqueue(new(importId), cancellationToken);
	}
}
```

`Enqueue`, `Schedule`, `ScheduleAt`, and `TriggerNow` return an opaque string invocation ID. The built-in scheduler currently creates a compact GUID-formatted value, but consumers must not parse it or depend on that format; storage integrations may use another string ID scheme.

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

Register the generated application job module:

```csharp
builder.Services.AddImmediateJobs(options =>
{
	options.UseEntityFrameworkCore<AppDbContext>(); // memory-primary single-server mode by default
	options.MaxParallelJobs = 16;
	options.PollingInterval = TimeSpan.FromSeconds(1);
}).AddHealthCheck();
```

The EF Core package adds `UseEntityFrameworkCore<TContext>()` in the same options callback. A durable provider implicitly selects single-server mode: memory is the live authority and every transition is written through to the database for restart recovery. Use `options.UseSingleServer()` to state that topology explicitly, `options.UseDistributed()` to make the database authoritative for multi-node coordination, or `options.UseInMemory()` for a non-durable development store.

Adding queue support introduces the required `QueueName` column on `immediate_jobs`, and invocation IDs are stored as strings with a maximum length of 256. Applications using EF Core storage must add a migration (or recreate a development database created from an earlier draft). The model supplies `"default"` as the queue database default so existing rows are backfilled safely.

## Recurring work

Code-defined schedules use five-field cron or six-field cron with seconds. See the [Cronos usage guide](https://github.com/HangfireIO/Cronos#Usage) for the supported syntax:

```csharp
[Handler, Job(Cron = "0 */5 * * * *", TimeZone = "Europe/Vienna")]
public sealed partial class CleanupSessionsJob(AppDbContext db)
{
	private ValueTask HandleAsync(NoPayload request, CancellationToken cancellationToken) =>
		new(db.DeleteExpiredSessions(cancellationToken));
}
```

Inject `CleanupSessionsJob.IScheduler` to trigger the code-defined job immediately:

```csharp
await scheduler.TriggerNow(cancellationToken);
```

Schedulers for jobs with a compile-time `Cron` do not expose dynamic schedule mutation. To manage named schedules at runtime, define a separate payloadless job without `Cron`:

```csharp
[Handler, Job("tenant-cleanup")]
public sealed partial class TenantCleanupJob(AppDbContext db)
{
	private ValueTask HandleAsync(NoPayload request, CancellationToken cancellationToken) =>
		new(db.DeleteExpiredSessions(cancellationToken));
}

await tenantCleanupScheduler.AddOrUpdateRecurring("tenant-42-cleanup", "0 0 3 * * *", "UTC", cancellationToken);
await tenantCleanupScheduler.RemoveRecurring("tenant-42-cleanup", cancellationToken);
```

Code schedules are re-asserted at startup. A dynamic schedule cannot replace a code-defined schedule with the same name. Storage uses a unique `(schedule name, scheduled UTC occurrence)` materialization key, so competing nodes produce one durable invocation for each occurrence.

## Immediate.Handlers behaviors for jobs

Background dispatch calls the generated Immediate.Handlers handler, so jobs use the same behavior pipeline as inline requests. A job request does not need a jobs-specific base type or interface. Implement the optional `IJobRequest` capability only when the handler or a behavior needs execution metadata; the worker then populates its non-persisted `JobDetails` immediately before entering the pipeline.

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

The `IJobRequest` constraint keeps this global behavior out of ordinary handlers and jobs that have not opted into execution metadata. Use Immediate.Handlers `[Behaviors(...)]` directly on a job to replace assembly behaviors, or put `[Behaviors(...)]` on a reusable custom attribute for a named job pipeline. Handler behaviors execute inside the retry boundary.

## Propagating scoped context

Use `IJobContextExtractor<TContext>` when a job needs request-scoped or ambient state that is not
part of its business payload. Capture runs while enqueueing in the caller's scope; restore runs in
the job's execution scope before the handler and its behaviors are resolved. The context value is
serialized with generated metadata, so it remains trimming- and Native AOT-safe.

```csharp
public sealed record UsageContext(Guid UserId, string TenantId);

public sealed class UsageContextExtractor(CurrentUsage current)
	: IJobContextExtractor<UsageContext>
{
	public string Key => "usage"; // stable across extractor type renames

	public ValueTask<UsageContext?> CaptureAsync(CancellationToken cancellationToken) =>
		ValueTask.FromResult(current.Value);

	public ValueTask RestoreAsync(UsageContext context, CancellationToken cancellationToken)
	{
		current.Value = context;
		return ValueTask.CompletedTask;
	}
}

[Handler, Job, UsesJobContext<UsageContextExtractor>]
public sealed partial class AuditUsageJob(CurrentUsage current)
{
	// The generated scheduler captures UsageContext and the worker restores it before this runs.
}
```

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
`CaptureAsync` when there is no context to persist, as is typical when recurring work is
materialized outside a request.

## Storage providers

- `Immediate.Jobs` includes the development-only in-memory provider, the memory-primary durable single-server topology, and the channel-backed worker pool.
- `Immediate.Jobs.EntityFrameworkCore` provides relational EF Core persistence and optimistic concurrency.

All providers implement `IJobStorage`. Single-server mode restores unfinished jobs and recurring schedules into memory when the process starts. Distributed mode uses provider leases; if a process dies, its lease expires and another node can acquire the invocation.

Queue-aware dispatch changes the provider acquisition seam to `AcquireDueJobsAsync(JobAcquisitionRequest, ...)`. Custom providers must honor the request's queue order, queue capacities, and per-job capacities when upgrading.

## Dashboard and monitoring API

Reference `Immediate.Jobs.Dashboard`, then map it after building the app:

```csharp
app.MapImmediateJobsDashboard("/jobs", options =>
	options.RequireAuthorization("operations"));
```

The package serves an embedded SPA, JSON monitoring endpoints, and a Server-Sent Events stream. Without an authorization policy, dashboard access is allowed only in the `Development` environment. The monitoring API supports snapshots, filtered jobs, recurring schedule actions, retry, and deletion.

## Testing

`Immediate.Jobs.Testing` provides `JobTestHarness`, a fake clock, advance-and-drain helpers, capture-only typed schedulers, enqueue assertions, and a single-job handler-pipeline runner. Delayed work, scheduled occurrences, timeout, and backoff tests do not need wall-clock sleeps.

## Diagnostics

| ID | Meaning |
|---|---|
| `IJOB001` | Invalid cron expression or literal schedule configuration |
| `IJOB002` | Duplicate persisted job name |
| `IJOB003` | Unsupported payload member/type |
| `IJOB004` | Invalid private `ValueTask HandleAsync(request, CancellationToken)` signature |
| `IJOB005` | Job handler class is not `partial` |
| `IJOB006` | A cron job declares a payload |
| `IJOB007` | NodaTime payload or context type used without `Immediate.Jobs.NodaTime` |
| `IJOB008` | Invalid retry/concurrency/timeout configuration |
| `IJOB009` | `[Job]` class is not also an Immediate `[Handler]` |
| `IJOB010` | Invalid queue name or concurrency configuration |
| `IJOB011` | `UsesQueue<T>` targets a type without `QueueDefinition` |
| `IJOB012` | Duplicate persisted queue name |
| `IJOB013` | `UsesJobContext<T>` targets an invalid extractor type |
| `IJOB014` | Unsupported context member/type |

## Observability

Executions emit activities from `ActivitySource` `Immediate.Jobs`, metrics from `Meter` `Immediate.Jobs`, structured logs scoped by job name/id/attempt, and scheduler/storage health checks. The metrics include enqueue/success/failure/retry counters, duration histograms, queue depth, and active workers.

The [Aspire sample](samples/Aspire/readme.md) runs the EF Core provider against an Aspire-managed PostgreSQL container and sends application logs, traces, metrics, and health status to the Aspire dashboard. It also exposes the Immediate.Jobs dashboard for job-specific history and operations.

## Benchmarks

The repository includes BenchmarkDotNet comparisons with Hangfire MemoryStorage and Quartz.NET for enqueue, direct dispatch, startup, and allocations. These are microbenchmarks of deliberately different framework APIs—not end-to-end durability or worker-latency measurements—so run them on the deployment target before drawing conclusions.

### Results

Latest `ShortRun` results from 21 July 2026: BenchmarkDotNet 0.15.8, .NET 8.0.22 Arm64 RyuJIT, Apple M3 Pro with 12 cores, macOS 26.5. Each result uses one launch, three warmup iterations, and three measurement iterations. Ratios use Immediate.Jobs as the baseline.

#### Enqueue

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
- Graceful worker drain: 30 seconds.

These values are configurable globally or, where applicable, on `[Job]`.

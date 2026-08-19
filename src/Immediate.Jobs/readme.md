# Immediate.Jobs

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

Immediate.Jobs is a reflection-free background job scheduler for .NET 8+ built on
[Immediate.Handlers](https://github.com/ImmediatePlatform/Immediate.Handlers). It provides generated, strongly typed
schedulers, an execution engine, and a development-only in-memory provider.

> [!IMPORTANT]
> Immediate.Jobs provides **at-least-once delivery**. Every handler that performs externally visible work must be
> idempotent. The in-memory provider is single-node, non-durable, and intended only for development, tests, and
> non-critical work.

## Installation

```console
dotnet add package Immediate.Jobs --prerelease
```

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

`Scheduler` is a nested generated type. The worker invokes the generated `SendWelcomeEmail.Handler`, so the same
handler and its ordinary Immediate.Handlers behaviors work both inline and in the background. `[Job]` is class-only and
is rejected unless the class is also marked `[Handler]`.

`EnqueueAsync`, `ScheduleAsync`, `ScheduleAtAsync`, and `TriggerNowAsync` each return a `JobHandle`. Its `Id` is an
opaque string invocation ID; consumers must not parse it or depend on its format.

Use the same generated scheduler to cancel a non-terminal invocation:

```csharp
JobHandle handle = await scheduler.EnqueueAsync(new(importId), cancellationToken);
await scheduler.CancelAsync(handle, cancellationToken);
```

Cancellation immediately records the durable job as `Cancelled`. If a worker already owns it, the current in-process
handler is not forcibly interrupted, but its stale completion cannot overwrite the cancelled state.

## Registration

Register the Immediate.Handlers pieces first, then call `services.AddXxxJobs()`, where `Xxx` is the application
identifier. By default this is the short form of the assembly name:

- `Web` generates `services.AddWebJobs()`.
- `Application.Web` generates `services.AddApplicationWebJobs()`.

Override the identifier with `[assembly: ImmediateAssemblyIdentifier("SomeIdentifier")]`.

```csharp
builder.Services.AddMyAppHandlers();
builder.Services.AddMyAppJobs()
	.ConfigureWorkers(options => options.MaxParallelJobs = 8)
	.ConfigureStorage(storage => storage.UseInMemory())
	.AddHealthCheck();
```

Because jobs are Immediate.Handlers handlers, omitting the corresponding generated `AddXxxHandlers()` method allows
enqueueing but causes execution to fail when the worker cannot resolve the handler or its behaviors.

Every application must call `ConfigureStorage` once. Use `UseInMemory()` for development, tests, or non-critical work.
For durable or distributed execution, install and configure one of the
[storage providers](https://github.com/ImmediatePlatform/Immediate.Jobs#packages).

`ConfigureWorkers` controls the worker count, polling interval, lease duration, shutdown timeout, and retention
periods. Call `DisableWorkers()` when a process may enqueue or inspect jobs but must not execute them:

```csharp
builder.Services.AddMyAppJobs()
	.DisableWorkers()
	.ConfigureStorage(storage => storage
		.UseEntityFrameworkCore<JobsDbContext>()
		.UseDistributed());
```

## Batches and continuations

Inject `IBatchScheduler` to create an atomic group. Generated schedulers expose strongly typed batch methods and return
handles that can be connected into chains, fan-out branches, and fan-in joins. Storage receives the entire batch in one
transaction; disposing without committing writes nothing.

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

Cancel every non-terminal member through the batch scheduler:

```csharp
await batches.CancelAsync(committed, cancellationToken);
```

`ContinuationTrigger.Success` is the default; `Failure` runs after every parent settles when at least one failed, and
`Complete` runs regardless of outcome. A running batch member can also expand its workflow through its injected
`JobDetails`; additions are buffered until the attempt succeeds so retries do not duplicate work.

Batches require a graph-capable SQL provider. Redis does not implement batches or continuations.

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

Larger priority values are acquired first. Queues at the same priority are considered in round-robin order, while jobs
within a queue remain ordered by due time and creation time. `Concurrency` is a per-node limit; zero is unbounded. It
composes with `Job.MaxConcurrency` and the global `MaxParallelJobs` limit.

When `Name` is omitted it is derived from the queue type (`TransactionalEmailQueue` becomes
`transactional-email-queue`). Jobs without `UsesQueue<TQueue>` use the unbounded priority-zero `default` queue. Queue
names are persisted with each invocation: keep an old definition until its non-terminal jobs drain, or migrate those
rows before renaming or removing it.

### Fair queues

Fair queues prevent one tenant's backlog from starving quieter tenants in the same queue. Supply a reusable tenant or
customer ID when enqueueing or scheduling work, then enable fair scheduling:

```csharp
await welcomeEmail.EnqueueAsync(
	new(userId, "v2"),
	groupId: tenantId,
	cancellationToken: cancellationToken
);

builder.Services.AddMyAppJobs()
	.UseFairQueues()
	.ConfigureStorage(storage => storage
		.UseEntityFrameworkCore<JobsDbContext>()
		.UseDistributed());
```

Fair queues rotate due work between tenant or customer groups so one large backlog cannot crowd out quieter groups.
They change which due job is selected next; they do not serialize a tenant's jobs or change queue priority and retry
rules. Null, empty, or whitespace group IDs remain ordinary ungrouped work. Use stable tenant or customer IDs, not a
different group ID for every job.

In-memory, EF Core, and LinqToDB support fair queues. Redis does not. See the
[queues and fairness documentation](https://immediateplatform.dev/docs/Immediate.Jobs/queues-and-fairness) for the
available settings and provider support.

## Recurring work

Code-defined schedules use five-field cron or six-field cron with seconds. See the
[Cronos usage guide](https://github.com/HangfireIO/Cronos#usage) for supported syntax:

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

Schedulers for jobs with a compile-time `Cron` do not expose dynamic schedule mutation. To manage named schedules at
runtime, define a separate payloadless job without `Cron`:

```csharp
[Handler, Job(Name = "tenant-cleanup")]
public sealed partial class TenantCleanupJob(AppDbContext db)
{
	private ValueTask HandleAsync(EmptyJobRequest request, CancellationToken cancellationToken) =>
		new(db.DeleteExpiredSessions(cancellationToken));
}

await tenantCleanupScheduler.AddOrUpdateRecurringAsync(
	"tenant-42-cleanup",
	"0 0 3 * * *",
	"UTC",
	cancellationToken);
await tenantCleanupScheduler.RemoveRecurringAsync("tenant-42-cleanup", cancellationToken);
```

## Immediate.Handlers behaviors

Background dispatch calls the generated Immediate.Handlers handler, so jobs use the same behavior pipeline as inline
requests. A request needs no jobs-specific base type. Implement `IJobRequest` only when a handler or behavior needs
execution metadata; the worker then populates its non-persisted `JobDetails` before entering the pipeline.

```csharp
public sealed record Payload(Guid UserId, string Template) : IJobRequest
{
	public JobDetails? JobDetails { get; set; }
}

[assembly: Behaviors(typeof(JobLoggingBehavior<,>))]

public sealed class JobLoggingBehavior<TRequest, TResponse>(ILogger<JobLoggingBehavior<TRequest, TResponse>> logger)
	: Behavior<TRequest, TResponse>
	where TRequest : IJobRequest
{
	public override async ValueTask<TResponse> HandleAsync(
		TRequest request,
		CancellationToken cancellationToken)
	{
		var details = request.JobDetails ?? throw new InvalidOperationException("Job details are unavailable.");
		logger.LogInformation("Starting {JobName} attempt {Attempt}", details.JobName, details.Attempt);
		return await Next(request, cancellationToken);
	}
}
```

The constraint keeps this global behavior out of ordinary handlers and jobs that have not opted into execution
metadata. Handler behaviors execute inside the retry boundary.

## Propagating scoped context

Derive an extractor from `JobContextExtractor<TContext>` to capture durable request context and restore it in the job's
execution scope. The context is serialized with generated metadata and remains trimming- and Native AOT-safe.

```csharp
public sealed record UsageContextSnapshot(Guid UserId, string TenantId);

public sealed class UsageContext
{
	public UsageContextSnapshot? Value { get; set; }
}

public sealed class UsageContextExtractor(UsageContext usage)
	: JobContextExtractor<UsageContextSnapshot>
{
	public override string Key => "usage";
	public override UsageContextSnapshot? Capture() => usage.Value;
	public override void Restore(UsageContextSnapshot context) => usage.Value = context;
}

[Handler, Job, UsesJobContext<UsageContextExtractor>]
public sealed partial class AuditUsageJob(UsageContext usage)
{
	// usage.Value contains the enqueueing scope's snapshot when this job runs.
}
```

Register the application-owned holder as scoped. Generated job registration adds the extractor as scoped. An
extractor's `Key` labels its stored context value and must be unique within a job. Return `null` from `Capture` when
there is no request context to store, which is common for recurring jobs.

Place multiple extractor markers on a reusable custom attribute when a family of jobs shares the same context policy.

## Monitoring and observability

Inject the scoped `JobMonitor` to read jobs, executions, recurring schedules, servers, batches, and dependency graphs,
or to perform administrative actions such as cancel, retry, pause, resume, and trigger. Inject `IJobMonitor` when code
needs only the read methods. `IJobStorage` is the provider contract and is not intended for application monitoring.

Executions emit activities from `ActivitySource` `Immediate.Jobs`, metrics from `Meter` `Immediate.Jobs`, structured
logs scoped by job name, ID, and attempt, and scheduler/storage health checks. Metrics include enqueue, success, failure,
and retry counters, duration histograms, queue depth, and active workers.

For a UI and HTTP API, install
[Immediate.Jobs.Dashboard](https://www.nuget.org/packages/Immediate.Jobs.Dashboard/).

## Diagnostics

| ID | Meaning |
|---|---|
| `IJOB0001` | `[Job]` class is not also an Immediate `[Handler]` |
| `IJOB0002` | Duplicate persisted job name |
| `IJOB0003` | Duplicate persisted queue name |
| `IJOB0004` | `NodaTime` is referenced without `Immediate.Jobs.NodaTime` |
| `IJOB0005` | Invalid retry, concurrency, or timeout configuration |
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

Invalid Immediate.Jobs runtime operations and states throw `ImmediateJobException`. Invalid method arguments, cron
expressions, serialized data, and missing records retain their standard exception types.

## Delivery and retention defaults

- Lease: 30 seconds, renewed while active.
- Attempts: 3 total, with exponential backoff and jitter from 5 seconds.
- Successful history: 24 hours.
- Failed history: 7 days.
- Execution history: the same lifetime as its owning job or batch.
- Graceful worker drain: 30 seconds.

These values are configurable globally or, where applicable, on `[Job]`.

## More information

- [Documentation](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
- [Packages and integrations](https://github.com/ImmediatePlatform/Immediate.Jobs#packages)

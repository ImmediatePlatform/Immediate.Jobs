# Immediate.Jobs — v1 Specification

**A fast, reflection-free background job scheduler for .NET, built with source generators.**

Status: Draft v0.2 · Date: 2026-07-22 · License: MIT · Target: .NET 8+

> **Since v0.1:** job continuations/chains and atomic batches — originally listed as v1 non-goals —
> are now implemented and part of the core package. See §2.8 and §3.2 here, and the full design in
> [`docs/batches-and-continuations.md`](docs/batches-and-continuations.md). SQS-style *fair queues*
> are also implemented; see [`docs/fair-queues.md`](docs/fair-queues.md).

---

## 1. Positioning

Immediate.Jobs is to Hangfire/Quartz what Immediate.Handlers is to MediatR: all job discovery, registration, DI wiring, and payload serialization happen at **compile time** via Roslyn source generators. There is zero runtime reflection, zero expression-tree compilation, and the whole thing is **Native AOT and trimming safe**.

**Value proposition:**

- Startup cost near zero — no assembly scanning.
- EnqueueAsync/dispatch is a direct generated call, not `MethodInfo.Invoke`.
- Serialization uses generated `JsonSerializerContext` — AOT-safe, fast.
- Compile-time errors for invalid cron expressions and unsupported payload types, instead of runtime surprises.
- Modern real-time dashboard included, MIT licensed (unlike Hangfire's licensing pressure and dated UI).

**Now implemented (was a v1 non-goal):** job continuations/chains and atomic batch jobs — a full
DAG workflow surface (fan-out, fan-in, diamonds, batch continuations, mid-job dynamic expansion). See
§2.8.

**Remaining non-goals** (candidates for later): workflow/saga compensation & rollback, result-passing
between parent and child jobs, human-in-the-loop suspension, calendar exclusions (holidays), and
multi-tenancy/schema isolation in storage providers.

---

## 2. Developer Experience

### 2.1 Defining jobs

`[Job]` applies only to a class that is also an Immediate.Handlers `[Handler]`. The handler request is the durable payload, and background execution calls the same generated handler used by inline callers. Method-level job attributes are deliberately unsupported.

```csharp
// Recurring cron job — name derived from class ("cleanup-sessions")
[Handler, Job(Cron = "0 */5 * * * *")] // 6-field, seconds supported
public sealed partial class CleanupSessionsJob(AppDbContext db)
{
    private async ValueTask HandleAsync(NoPayload _, CancellationToken ct)
    {
        await db.Sessions.Where(s => s.ExpiresAt < DateTimeOffset.UtcNow)
            .ExecuteDeleteAsync(ct);
    }
}

// Enqueueable job with a payload — explicit name pinned for rename-stability
[Handler, Job("send-welcome-email", MaxAttempts = 5, Timeout = "00:02:00")]
public sealed partial class SendWelcomeEmail(IEmailSender sender)
{
    public sealed record Payload(Guid UserId, string Template);

    private async ValueTask HandleAsync(Payload payload, CancellationToken ct)
        => await sender.SendAsync(payload.UserId, payload.Template, ct);
}
```

### 2.2 Enqueueing and scheduling

No central dispatcher, no `Send(object)`-style indirection. Following the Immediate.Handlers pattern, the generator emits a nested `Scheduler` class **per job**. You inject the specific job's scheduler and call it — the call site names the job explicitly, navigation (F12) goes straight to the job class, and unused jobs are trivially detectable.

```csharp
public sealed class SignupService(SendWelcomeEmail.Scheduler welcomeEmail)
{
    public async Task SignUpAsync(Guid userId, CancellationToken ct)
    {
        // ...
        await welcomeEmail.EnqueueAsync(new(userId, "v2"), ct);                       // ASAP
        await welcomeEmail.ScheduleAsync(new(userId, "v2"), TimeSpan.FromHours(1), ct); // delayed
        await welcomeEmail.ScheduleAtAsync(new(userId, "v2"), runAt, ct);             // at time
    }
}
```

The generated `Scheduler` is a thin, sealed, scoped class over `IJobStorage` + the generated serializer — fully typed and AOT-safe. Resolve it from the caller's request or DI scope. Singleton consumers cannot inject a scoped scheduler directly and instead create a scope with `IServiceScopeFactory` for each unit of work.

Cron jobs declared via attribute are registered automatically at startup and expose a payload-less scheduler (`CleanupSessionsJob.Scheduler.TriggerNowAsync(ct)`); dynamic recurring schedules are covered in §2.7.

Every enqueue or trigger operation returns a `JobHandle` — an opaque value carrying the invocation's
`.Id` (still available as `handle.Id` when only the string is wanted). The built-in scheduler uses
compact GUID-formatted strings, but callers and storage providers must not parse or depend on that
format. IDs are client-generated *before* storage, so a handle carries a real ID immediately — even
for a job still buffered in an uncommitted batch — which is what makes intra-batch continuations
resolvable before commit (§2.8).

### 2.3 Registration

```csharp
builder.Services.AddImmediateJobs(o =>
{
    o.UseEntityFrameworkCore<AppDbContext>(); // durable, memory-primary single-server mode by default
    // o.UseSingleServer();               // explicit form of the default durable topology
    // o.UseDistributed();                // opt in to DB-primary multi-node coordination
    o.MaxParallelJobs = 16;               // global worker cap
    o.PollingInterval = TimeSpan.FromSeconds(1); // fallback poll; providers may push
});

app.MapImmediateJobsDashboard("/jobs");   // optional, from Immediate.Jobs.Dashboard
```

Jobs may be assigned to compile-time queue definitions with `[UsesQueue<TQueue>]`. Queue definitions provide a stable persisted name, a descending integer priority, and a per-node concurrency limit. Unassigned jobs use the built-in `default` queue. Queue and job concurrency limits are both applied during acquisition so work waiting on one limit does not occupy a worker slot.

`AddImmediateJobs()` calls into generated code that registers every discovered Immediate.Handler plus its nested `Scheduler` — the user never lists jobs manually.

### 2.4 Attribute surface

| Property         | Type    | Default             | Notes                                                                                                                                                                                                                                                                                                                                       |
| ---------------- | ------- | ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| name (ctor)      | string? | derived             | Unique job id, persisted identity in storage. Defaults to kebab-cased class name (`SendWelcomeEmail` → `send-welcome-email`); specify explicitly for rename-stability. Generator errors on duplicates (incl. short-name collisions across namespaces). Renaming a class with in-flight jobs orphans them — pin the old name or drain first. |
| `Cron`           | string? | null                | 5 or 6-field cron; validated at compile time                                                                                                                                                                                                                                                                                                |
| `TimeZone`       | string? | UTC                 | IANA id (Windows ids also resolve at run time). Checked non-empty at compile time and resolved on the runtime host — intentionally **not** validated against the build machine's tz database, so generated output stays host-independent                                                                                                       |
| `MaxAttempts`    | int     | 3                   | Total attempts including first run                                                                                                                                                                                                                                                                                                          |
| `Timeout`        | string? | null                | `TimeSpan` format; per-execution timeout                                                                                                                                                                                                                                                                                                    |
| `MaxConcurrency` | int     | unbounded           | Max parallel executions of this job type per node                                                                                                                                                                                                                                                                                           |
| `OverlapPolicy`  | enum    | `Skip`              | `Skip` \| `Queue` \| `Concurrent` — behavior when a scheduled recurring occurrence is due while the previous invocation is active                                                                                                                                                                                                          |
| `Backoff`        | enum    | `ExponentialJitter` | `Fixed` \| `Exponential` \| `ExponentialJitter`                                                                                                                                                                                                                                                                                             |
| `BackoffBase`    | string  | "00:00:05"          | Base delay for backoff                                                                                                                                                                                                                                                                                                                      |

### 2.5 Compile-time diagnostics

The generator ships an analyzer emitting, at minimum:

- `IJOB001` invalid cron expression
- `IJOB002` duplicate job name
- `IJOB003` payload type not serializable / unsupported member
- `IJOB004` job method has invalid signature (private `ValueTask HandleAsync(request, CancellationToken)`)
- `IJOB005` `[Job]` class not `partial`
- `IJOB006` cron job uses a request other than the `NoPayload` marker
- `IJOB007` payload contains NodaTime types but `Immediate.Jobs.NodaTime` is not referenced
- `IJOB008` retry/concurrency/timeout configuration is invalid
- `IJOB009` `[Job]` class is not also marked with Immediate.Handlers `[Handler]`

### 2.6 Immediate.Handlers behaviors for jobs

Every job uses its normal Immediate.Handlers pipeline because the worker invokes the generated handler. Job requests need no jobs-specific interface. A request may implement the optional `IJobRequest` capability when it needs non-persisted execution metadata; the worker attaches those details before entering the pipeline:

```csharp
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

- Register constrained job behaviors through Immediate.Handlers `[assembly: Behaviors(...)]`. The constraint excludes ordinary handlers and jobs that do not opt into `IJobRequest`.
- Use `[Behaviors(...)]` on one job, or a reusable attribute annotated with `[Behaviors(...)]`, to replace assembly-wide behaviors for that subset.
- `JobDetails` exposes `JobName`, `JobId`, `QueueName`, `Attempt`, `CreatedAt`, and `ScheduledAt`; the cancellation token remains the normal behavior method parameter.
- The handler pipeline runs inside the retry boundary; built-in concerns such as lease heartbeat, timeout, serialization, and ambient-context restoration remain infrastructure concerns.

Ambient request state is a separate capture/restore lifecycle, not a handler behavior. A job opts in to
a typed extractor whose serializable value is captured by its generated scheduler and restored in
the worker's execution scope before handler and behavior resolution:

```csharp
public sealed record UsageContext(Guid UserId, string TenantId);

public sealed class UsageContextExtractor(CurrentUsage current)
    : IJobContextExtractor<UsageContext>
{
    public string Key => "usage";

	public UsageContext? Capture() => current.Value;

	public void Restore(UsageContext context) => current.Value = context;
}

[Handler, Job, UsesJobContext<UsageContextExtractor>]
public sealed partial class SendInvoiceJob { /* ... */ }
```

`Key` is the stable name of the persisted envelope slice and must be unique among the extractors
used by a job. `Capture` returns `null` when there is no context to persist. Context values use
generated `JsonTypeInfo`, with the same supported-shape and Native AOT guarantees as job payloads.
Capture failures fail enqueue; restore failures fail the current attempt and enter normal retry
handling.

Reusable marker attributes apply a named set of extractors without an assembly-wide default:

```csharp
[UsesJobContext<UsageContextExtractor>]
[UsesJobContext<CorrelationContextExtractor>]
public sealed class WebJobAttribute : Attribute;

[Handler, Job, WebJob]
public sealed partial class SendInvoiceReminderJob { /* ... */ }
```

The generator registers referenced extractors and generated schedulers as scoped services. This
lets capture see caller-scoped state. A singleton enqueuer must create a scope before resolving the
scheduler, just as it would for a scoped `DbContext`:

```csharp
public sealed class InvoicePoller(IServiceScopeFactory scopeFactory)
{
    public async ValueTask EnqueueAsync(Guid invoiceId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<SendInvoiceJob.Scheduler>();
        await scheduler.EnqueueAsync(new(invoiceId), ct);
    }
}
```

### 2.7 Dynamic recurring jobs

Recurring jobs can be added, updated, and removed at runtime with full parity to attribute-declared crons (same overlap policy, retry, timeout, and dashboard treatment):

```csharp
await CleanupSessionsJob.Scheduler.AddOrUpdateRecurringAsync(
    name: $"cleanup-{tenantId}", cron: "0 0 3 * * *", timeZone: tz, ct);
await CleanupSessionsJob.Scheduler.RemoveRecurringAsync($"cleanup-{tenantId}", ct);
```

Dynamic schedules are persisted in storage (they survive restarts and are visible on all nodes). Attribute-declared crons are reconciled at startup: current definitions are re-asserted, obsolete persisted definitions are removed, and active definitions are marked "code-defined" in the dashboard (pausable, not deletable); dynamic ones are left unchanged and remain fully editable. Cron strings from runtime input are validated at call time with a clear exception.

### 2.8 Batches & continuations (DAG workflows)

Atomic batches and continuations are in the core package. They keep the **generated scheduler as the
subject** — `sendEmail.AddToBatch(batch, payload)` and `two.ScheduleAfterAsync(oneHandle, payload)` read
exactly like `sendEmail.EnqueueAsync(payload)`. Nothing but data is ever persisted: no closures, no
expression trees, no `MethodInfo` — so no reflective dispatch is introduced. Full design in
[`docs/batches-and-continuations.md`](docs/batches-and-continuations.md).

**Atomic batches.** `IJobBatchScheduler` is a scoped service. `Begin()` opens an in-memory buffer;
each generated scheduler inherits typed `AddToBatch`/`AddToBatchAt` methods that buffer a
fully-serialized `JobRecord`; `CommitAsync`
flushes the whole buffer in **one atomic unit** (all rows or none). Disposing without committing rolls
back. Because storage is touched only at commit, retrying the whole method after a mid-loop failure
cannot double-enqueue. `RunAsync(body)` is sugar over `Begin → body → CommitAsync`.

**Continuations.** Every generated scheduler inherits `ScheduleAfterAsync(parent, payload)` — it
schedules *its own* job to run after `parent`, returning a new `JobHandle` so chains compose. Passing
several parents as a collection (`[a, b, c]`) expresses **fan-in**; calling `ScheduleAfterAsync` on the same parent twice
expresses **fan-out**; diamonds and continuations-of-continuations follow naturally. Parents may be a
`JobHandle`, a collection of handles, or a committed batch's `BatchHandle` (a job that runs once the
whole batch completes). Continuations work inside or outside a batch; a standalone continuation whose
parent is already terminal is evaluated immediately rather than waiting forever.

**Mid-job dynamic expansion.** A running member can expand the workflow at execution time using its
own `JobDetails` (from `IJobRequest`) as the "I am running inside this job" token:
`ScheduleAfter(JobDetails, …)` is *gated* (buffered until the current job succeeds, so retries don't
double-schedule) and `AddToBatchAsync(JobDetails, …)` is *concurrent* (written immediately). A
`ContinuationOptions` value (`Detached` | `BesideContinuations` | `BeforeContinuations`, default
`BeforeContinuations`) chooses how the current job's existing waiters relate to the new work; the
default performs an additive splice so downstream waits on both.

**Triggers.** `ContinuationTrigger.Success` (default) cancels the child (cascading to its
subtree) if any parent does not succeed. `Failure` waits for every parent and runs when at least one
parent failed; for a batch parent, this means the aggregate batch state is `Failed`. `Complete` runs
the child once all parents reach any terminal state (for cleanup/notification steps).

**Non-goals for this feature:** result-passing between parent and child (a continuation observes a
parent's *outcome*, not its return value), human-in-the-loop suspension, and saga/compensation
rollback.

New compile-time diagnostics accompany the feature (e.g. `IJOB020` rejects the contradictory
`AddToBatchAsync(JobDetails, Detached)`); batch-tracking calls from a job with no batch are guarded at run
time.

---

## 3. Architecture

### 3.1 Packages

| Package or project                   | Contents                                                                                                                                                                                |
| ------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Immediate.Jobs`                     | Packaging-only NuGet project containing the Shared runtime assemblies and the TFM-appropriate analyzer/generator assets                                                                |
| `Immediate.Jobs.Shared`              | Non-packable runtime project: scheduler hosted service, worker pool, storage abstraction, in-memory provider, generated-code contracts                                                  |
| `Immediate.Jobs.Analyzers`           | Non-packable Roslyn analyzer project embedded into the `Immediate.Jobs` package                                                                                                         |
| `Immediate.Jobs.Generators`          | Non-packable Roslyn source-generator project embedded into the `Immediate.Jobs` package                                                                                                 |
| `Immediate.Jobs.EntityFrameworkCore` | EF Core adapter — works with any relational EF provider; convenience over raw speed                                                                                                     |
| `Immediate.Jobs.LinqToDB`            | Provider-neutral LinqToDB adapter — schema-compatible with EF Core and validated on PostgreSQL, SQLite, and SQL Server                                                                  |
| `Immediate.Jobs.Dashboard`           | Embedded dashboard middleware + compiled Vue SPA assets                                                                                                                                 |
| `Immediate.Jobs.NodaTime`            | Optional: `Instant`/`Duration`/`DateTimeZone` overloads on generated schedulers; NodaTime STJ converters wired into generated serializer contexts                                       |
| `Immediate.Jobs.Testing`             | `JobTestHarness` (in-memory provider + `FakeTimeProvider`, advance-time-and-drain), typed enqueue assertions, capture-only scheduler call recorders, run-single-job-through-pipeline helper |

Both relational adapters use the same five-table schema, UTC-tick timestamps, enum values, and
optimistic concurrency stamps. EF applications own migrations; LinqToDB applications explicitly
bootstrap fresh databases with `CreateImmediateJobsSchemaAsync`. Database drivers remain application
dependencies rather than adapter dependencies.

### 3.2 Storage abstraction

`IJobStorage` is the single seam providers implement — all async and `CancellationToken`-aware.
Grouped by concern:

- **Enqueue & claim:** `EnqueueAsync(JobRecord)`; `AcquireDueJobsAsync(JobAcquisitionRequest)` —
  atomically claims due work honoring per-queue and per-job capacities in priority order (the
  concurrency-critical call); `RenewLeaseAsync` / `CompleteAsync` / `FailAsync(nextRetryAt |
  deadLetter)`; `SetExecutionTelemetryAsync` persists the latest attempt's trace/span correlation
  after an invocation is claimed.
- **Continuations & batches:** `EnqueueContinuationAsync` (child + edges, atomic),
  `EnqueueBatchAsync` (header + members + edges, atomic), `CompleteWithContinuationsAsync` (complete +
  flush gated dynamic continuations), `AddBatchJobAsync`, `CancelBatchAsync`, `DeleteBatchAsync`.
- **Recurring:** `UpsertRecurringAsync` / `PauseRecurringAsync` / `ResumeRecurringAsync` /
  `RemoveRecurringAsync` / `RemoveObsoleteCodeDefinedRecurringAsync`; `GetDueRecurringAsync` +
  `MaterializeRecurringAsync` (create occurrence + advance schedule, atomic).
- **Reads (dashboard/monitoring):** `GetMonitoringSnapshotAsync`, `QueryJobsAsync`,
  `GetJobStatusAsync`, `QueryBatchesAsync`, `QueryBatchMembersAsync`, `GetBatchStatusAsync`,
  `GetBatchGraphAsync`.
- **Maintenance & health:** `RetryAsync`, `DeleteAsync`, `PurgeAsync(retention)`, `HeartbeatAsync`,
  `IsHealthyAsync`, `InitializeAsync`.

Single-server mode additionally uses the small `IJobStorageReplica` capability to mirror the exact set
of jobs its authoritative in-memory queue selected. Payloads are stored as JSON, serialized via the
generated `JsonSerializerContext`. A pluggable `IJobSerializer` exists with the generated STJ
implementation as default.

### 3.3 Execution model

- A hosted service (`JobSchedulerService`) runs the acquire → dispatch loop; a `Channel`-based worker pool executes handlers with per-type and global concurrency limits.
- Each execution creates a DI scope; the generated invoker restores ambient context, attaches `JobDetails`, resolves the Immediate.Handlers-generated nested `Handler`, and calls it directly without reflection.
- Cron evaluation uses a vetted cron library (Cronos) at runtime for dynamic schedules; literal attribute crons are additionally validated at compile time.
- Time is abstracted via `TimeProvider` for testability.
- Graceful shutdown: stop acquiring, signal `CancellationToken`, drain running jobs up to a configurable shutdown timeout; undrained jobs' leases expire and another node picks them up.

### 3.4 Multi-node coordination & delivery guarantees

- **Default durable topology:** single-server, memory-primary. EF Core storage is a synchronous write-through resilience ledger; unfinished jobs and recurring schedules are restored into memory after restart. `UseSingleServer()` selects this explicitly.
- **Distributed opt-in:** `UseDistributed()` makes durable storage authoritative and enables peer-to-peer multi-node claiming.
- **Scale-out model:** peer-to-peer — every node both schedules and executes. Coordination happens entirely through storage; no leader election, no inter-node communication.
- **Claiming:** `AcquireDueJobsAsync` uses EF Core optimistic concurrency tokens so nodes never successfully claim the same version of a row.
- **Leases & heartbeats:** claimed jobs carry a lease (default 30s) renewed by a heartbeat while running. If a node dies, the lease expires and the job becomes claimable again → **at-least-once** delivery. Docs prominently state handlers must be idempotent.
- **Recurring jobs:** each scheduled occurrence is materialized as a one-shot job row keyed by `(jobName, scheduledOccurrence)` with a unique constraint, so N nodes materializing the same occurrence produce exactly one execution.
- **In-memory provider:** best-effort only, single-node, no durability — clearly documented; intended for dev/test and non-critical jobs.

### 3.5 Failure handling

- **Retries:** on exception, reschedule with configured backoff (default exponential + jitter, base 5s, `MaxAttempts` 3). `attempt` count and last error stored per job.
- **Timeouts:** per-job `Timeout` triggers cancellation of the handler's token; a timeout counts as a failed attempt.
- **Dead-letter:** exhausted jobs move to `Failed` state, retained (default 7 days, configurable), visible and re-runnable from the dashboard.
- **Poison-safety:** an unhandled exception in one job never affects the worker pool or other jobs.
- **Job states:** `Scheduled → Pending → Active → Succeeded | Failed | Cancelled`. Continuation
  children are created parked in `AwaitingContinuation` and flip to `Pending` (or `Cancelled`) as
  their incoming edges are satisfied; `AwaitingParameters` is a reserved, currently-unused state for a
  future deferred-input capability (providers treat it as non-acquirable, like `AwaitingContinuation`).
  Succeeded history retained (default 24h, configurable).

---

## 4. Dashboard (`Immediate.Jobs.Dashboard`)

- **Stack:** Vue 3 + TypeScript SPA with Vue Router, TanStack Query, VueUse, and Tailwind CSS, compiled to static assets embedded in the NuGet package (no Node required by consumers). Small bundle target: < 200 KB gzipped.
- **Hosting:** `app.MapImmediateJobsDashboard("/jobs")` maps the SPA plus a JSON API under the same prefix.
- **Real-time:** Server-Sent Events for live updates (no SignalR dependency — keeps core lean and works everywhere); SPA falls back to polling.
- **Views:** overview (throughput, queue depth, success/failure sparklines), recurring jobs (next/last run, pause state), job list per state with filtering/search, job detail (payload, attempts, errors, timings, incoming dependencies), batches (progress: total/pending/succeeded/failed/cancelled, per-member status, and the continuation **dependency graph**), servers/nodes (heartbeat, active workers).
- **Telemetry links:** applications configure provider-specific trace and log destinations with
  `AddTelemetryLink`; job detail exposes the latest execution trace/span and external actions.
- **Actions:** trigger recurring job now · retry failed · delete failed · pause/resume recurring schedule · cancel a running batch · delete a terminal batch.
- **Auth:** dashboard endpoints integrate with ASP.NET Core authorization — `MapImmediateJobsDashboard(o => o.RequireAuthorization("policy"))`. Default: allowed only in `Development` unless a policy is configured (Hangfire's local-only default, but explicit).
- **Read/write split:** the JSON+SSE monitoring API is a documented, stable surface usable without the SPA.

---

## 5. Observability

- **Traces:** `ActivitySource` `Immediate.Jobs` — one activity per execution attempt; enqueue
  propagates trace context so the handler's trace links to the enqueuer's. The latest attempt's trace
  ID, span ID, and start time are persisted on the job record for dashboard correlation.
- **Metrics:** `Meter` `Immediate.Jobs` — counters `jobs.enqueued/succeeded/failed/retried`, histogram `job.duration`, gauges `queue.depth`, `workers.active`, all tagged by job name.
- **Logging:** structured `ILogger` with scope `{JobName, JobId, Attempt}`; log levels follow .NET conventions (failures = Error on final attempt, Warning on retryable). A stable `JobId` query spans every retry even though only the latest attempt has a persisted direct trace link.
- **Health checks:** `AddImmediateJobs().AddHealthCheck()` reports scheduler liveness (loop heartbeat) and storage connectivity.

---

## 6. Testing & Quality Bar

- `Immediate.Jobs.Testing` ships in v1 (see packages table); `TimeProvider` injection throughout core makes cron/backoff behavior deterministic under `FakeTimeProvider`.
- Generator snapshot tests (Verify), analyzer tests for every diagnostic, and incremental-generator cacheability tests asserting the pipeline re-runs nothing when unrelated source changes.
- .NET 10 storage conformance matrix: EF Core and LinqToDB against PostgreSQL, file-backed SQLite,
  and SQL Server, including optimistic contention, lease recovery, batches/continuations, recurring
  deduplication, monitoring/maintenance, and cross-adapter schema compatibility. PostgreSQL and SQL
  Server use one Testcontainers instance per assembly; SQLite remains embedded and isolated per test.
- Benchmarks (BenchmarkDotNet) vs Hangfire and Quartz.NET: enqueue throughput, dispatch latency, startup time, allocations — published in the README.
- Native AOT sample app compiled in CI to guard the reflection-free claim.

---

## 7. Milestones

| Milestone                      | Scope                                                                                                                                                                                                  |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **M1 — Core**                  | Generator + attribute model, diagnostics, in-memory provider, worker pool, cron/delayed/immediate jobs, Immediate.Handlers integration, dynamic recurring jobs, retries/timeouts/dead-letter, graceful shutdown |
| **M2 — Durable & distributed** | Storage abstraction finalized, EF Core adapter with optimistic leases, single-server recovery, and multi-node tests                                                                                  |
| **M3 — Dashboard**             | Monitoring API (JSON + SSE), Vue SPA, actions (trigger/retry/delete/pause), auth integration                                                                                                           |
| **M4 — Polish & ship**         | `Immediate.Jobs.Testing`, OTel + metrics + health checks, AOT CI, benchmarks, docs site, v1.0 to NuGet                                                                                                 |
| **M5 — Batches & continuations** | Atomic batches, job→job and batch continuations, fan-out/fan-in/diamonds, mid-job dynamic expansion, `AwaitingContinuation` state + edge/counter storage across providers, batch dashboard views & graph, new diagnostics |
| **M6 — Fair queues**           | Per-group backlog rotation and noisy-neighbor prioritization for in-memory, EF Core, LinqToDB, and single-server acquisition; persisted group visibility in the dashboard |

The shipped fair-queue behavior and provider support matrix are documented in
[`docs/fair-queues.md`](docs/fair-queues.md).

---

## 8. Open Questions

None open for shipped features.

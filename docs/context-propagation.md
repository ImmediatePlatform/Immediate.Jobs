# Design: Ambient context capture & restore for jobs

**Status:** Implemented · **Date:** 2026-07-21 · **Package:** Immediate.Jobs

## 1. Problem

Cross-cutting job concerns (audit logging, permission checks, tenant resolution) frequently
depend on **request-scoped ambient state** — the current user, tenant, correlation id, culture —
that ASP.NET populates during an HTTP request. When the same work runs in the background worker,
that state is gone: the job executes in a fresh DI scope with no HTTP request, so a behavior that
reads `ICurrentUser` / `ITenantContext` sees nothing.

Execution-time Immediate.Handlers `Behavior<,>` implementations cannot solve this, because the data is not present at execution time. It
must be **captured while enqueuing** (still inside the originating scope) and **restored before the
job runs**.

## 2. Precedent already in the codebase

This exact pattern already exists for W3C distributed tracing:

- **Capture** — `JobScheduler<TPayload>.ScheduleAtAsync` (`src/Immediate.Jobs.Shared/JobSchedulers.cs`) calls
  `TraceContextCapture.Current()` and persists `JobRecord.TraceParent` / `TraceState`.
- **Restore** — `JobSchedulerService` (`src/Immediate.Jobs.Shared/JobSchedulerService.cs`) parses those
  back into an `ActivityContext` and starts the execution activity with that parent.

Tracing works from today's **singleton** scheduler only because `Activity.Current` is an ambient
`AsyncLocal`. This design generalizes the same capture/restore lifecycle into a **pluggable,
opt-in, typed** mechanism that also covers state living in scoped DI.

## 3. Goals / non-goals

**Goals**
- Reusable extractors that can be applied to many jobs without duplication.
- Strongly-typed context objects, AOT/trimming-safe serialization (same bar as payloads).
- Restored state lands in the **same scoped services** existing behaviors already read, so no
  behavior code changes are required.
- Opt-in per job; jobs that don't use it pay nothing.

**Non-goals**
- Replacing or merging Immediate.Handlers behaviors; context propagation remains a separate lifecycle hook that completes before the handler pipeline.
- Capturing arbitrary object graphs — context types follow the same serializable-shape rules as
  payloads.

## 4. Public surface

### 4.1 Extractor abstraction (core package)

```csharp
namespace Immediate.Jobs.Shared;

public abstract class JobContextExtractor
{
	// Stable slice key in the persisted envelope. Defined on the extractor (not derived from the
	// type name) so renaming/moving the extractor type never orphans in-flight records.
	public abstract string Key { get; }
}

public abstract class JobContextExtractor<TContext> : JobContextExtractor
{
	// Runs at enqueue, in the caller's scope. Reads ambient/scoped services.
	// Return null to signal "nothing to capture" (e.g. no active request).
	public abstract TContext? Capture();

	// Runs at execution, in the job's scope. Repopulates services from the captured value.
	public abstract void Restore(TContext context);
}
```

`Key` is read at runtime by the generated capture/restore code, so it can be any stable string and
survives type renames. Two registered extractors sharing a `Key` collide in the envelope; that is
guarded at runtime (§8) rather than at compile time, since the value isn't a compile-time constant.

### 4.2 A reusable extractor (user code)

```csharp
// Durable data serialized into the job's context envelope.
public sealed record UsageContextSnapshot(Guid UserId, string TenantId);

// Application-owned scoped state used by request and job code.
public sealed class UsageContext
{
	public UsageContextSnapshot? Value { get; set; }
}

public sealed class UsageContextExtractor(UsageContext usage)
	: JobContextExtractor<UsageContextSnapshot>
{
	public override string Key => "usage";   // stable; renaming this class won't break in-flight records

	public override UsageContextSnapshot? Capture() => usage.Value;

	public override void Restore(UsageContextSnapshot snapshot) => usage.Value = snapshot;
}

builder.Services.AddScoped<UsageContext>();
```

`UsageContext` is the service injected from DI. The request pipeline populates it in the enqueueing
scope, and jobs or behaviors inject it to consume the restored state. `UsageContextSnapshot` is not
a DI service: it is the durable value returned by `Capture`, serialized with the job, and supplied
to `Restore` in the execution scope. The generator registers each referenced extractor as scoped;
the application registers its own scoped holder and any dependencies used to populate it.

The snapshot type is defined **with the extractor**, not the job — that is what makes it reusable.

### 4.3 Opt-in marker (core package + generator)

Direct form:

```csharp
[Handler, Job, UsesJobContext<UsageContextExtractor>]
public sealed partial class SendInvoiceJob { /* ... */ }
```

**Reusable marker form (decided).** A custom attribute annotated with `[UsesJobContext<T>]` applies
its extractor(s) to every job it is placed on — the Immediate.Handlers "named pipeline" trick, which
gives reuse without the blast radius of an assembly-wide default:

```csharp
[UsesJobContext<UsageContextExtractor>]
[UsesJobContext<CorrelationExtractor>]
public sealed class WebJobAttribute : Attribute;

[Handler, Job, WebJob]                       // captures/restores both contexts
public sealed partial class SendInvoiceJob { /* ... */ }
```

- `[UsesJobContext<TExtractor>]` is a generic attribute (`AttributeTargets.Class`,
  `AllowMultiple = true`); a job may stack several, directly and/or via marker attributes.
- **Assembly-wide `[assembly: UsesJobContext<T>]` is out of scope** — the reusable marker covers
  "apply to a family of jobs" without silently capturing context on jobs that don't need it.
- The generator emits `TryAddScoped` for every referenced extractor (mirrors how behaviors are
  registered). Application-owned dependencies such as `UsageContext` still require their normal
  DI registrations.

## 5. Runtime & generator changes

### 5.1 Context envelope on the record

Add one nullable column, mirroring `TraceParent`:

```csharp
// JobModels.cs — JobRecord
/// <summary>Serialized ambient-context envelope captured while enqueueing.</summary>
public string? Context { get; init; }
```

The envelope is a JSON object keyed by each extractor's `Key` (§4.1):
`{ "usage": { "UserId": "…", "TenantId": "…" } }`. One column keeps the storage change minimal and
matches the existing trace-column shape.

### 5.2 Capture — generated scheduler (enqueue)

`JobScheduler<TPayload>.ScheduleAtAsync` gains a hook the generated subclass overrides:

```csharp
// base
protected virtual string? CaptureContext() => null;

// CreateRecord, before building the record:
var context = CaptureContext();
var record = new JobRecord { /* … */ Context = context };
```

The generator emits the override per job: it injects the job's extractors (constructor
parameters on the generated `Scheduler`), calls each `Capture`, serializes each non-null
`TContext` with generated AOT-safe `JsonTypeInfo`, and returns the assembled envelope (or `null`
when nothing was captured).

Because the scheduler is **scoped** (§6), the extractors and the ambient/scoped services they read
resolve from the caller's request scope.

### 5.3 Restore — generated invoker (execution)

`IJobInvoker.InvokeAsync(scopedServices, execution)` already receives the per-execution scope and
the `JobExecution` (which carries the record, hence the envelope). The generated `Invoker` runs
restore **before** the handler/behavior pipeline:

```csharp
// generated Invoker.InvokeAsync, before resolving the handler:
if (execution.Record.Context is { } envelope)
{
    var extractor = ServiceProviderServiceExtensions.GetRequiredService<UsageContextExtractor>(scopedServices);
    if (TryReadSlice<UsageContextSnapshot>(envelope, extractor.Key) is { } ctx)
		extractor.Restore(ctx);
}
// … then existing handler + behavior invocation …
```

Restore runs at the top of execution, so the repopulated scoped services are visible to the job
behavior pipeline, the handler, and the handler's own Immediate.Handlers behaviors — no behavior
code changes needed. The singleton `Invoker` is fine here: it resolves extractors from
`scopedServices`, not from itself.

### 5.4 Serialization / AOT

Context types reuse `JsonMetadataEmitter`: for each distinct `TContext` referenced by a job's
extractors, the generator emits `JsonTypeInfo` alongside the payload metadata, and capture/restore
call the factory overloads on `IJobSerializer`. Context types are therefore subject to the same
supported-shape validation as payloads (public members, bindable constructor, no delegates/pointers,
NodaTime requires the integration package), surfaced via new analyzer diagnostics.

## 6. Scheduler lifetime: singleton → **scoped** (decided)

Generated schedulers move from `TryAddSingleton` to `TryAddScoped` in
`Templates/ServiceCollectionExtensions.sbntxt`.

**Why it's safe:** `JobScheduler<TPayload>` holds only immutable dependencies (`IJobStorage`,
`IJobSerializer`, `TimeProvider`, name/queue, the payload factory) and no per-instance mutable
state. Nothing in the runtime resolves generated schedulers — the worker enqueues recurring work by
building `JobRecord`s directly (`MaterializeRecurringAsync`), never via a scheduler. Only user code
resolves a generated `Scheduler`.

**Why it's better:** a scoped scheduler naturally shares the request scope, so capture reads
request-scoped services directly (not only `AsyncLocal`-backed accessors). This matches the DI
posture ASP.NET apps already expect (e.g. `DbContext` is scoped).

**DI consequence:** a **singleton** consumer (e.g. an `IHostedService` that enqueues) cannot inject
`Scheduler` directly — resolving a scoped service from the root throws. Such consumers create a
scope (`IServiceScopeFactory.CreateScope()`) per unit of work, or inject a factory. This is standard
scoped-service guidance (same as `DbContext`) and should be documented in the readme.

## 7. Recurring / cron jobs

Recurring materialization happens inside the worker with no request scope, so request-oriented
extractors have nothing to capture — `Capture` returns `null` and no envelope slice is written
(same limitation the trace capture has for cron). Extractors must tolerate "no context available";
the nullable `Capture` return models this explicitly.

## 8. Failure & robustness policy (decided)

| Point | Behavior | Rationale |
| --- | --- | --- |
| `Capture` throws (enqueue) | **Propagate to the caller** (enqueue throws) | Caller is in-request and can decide; silently dropping context is worse than a visible failure. |
| Envelope slice references a `Key` with no matching registered extractor | Skip that slice, log | A durable record can outlive the extractor that produced it (extractor removed in a later deploy); the job must still run. |
| Two registered extractors share a `Key` | Throw at capture (collision) | Deterministic envelope; surfaces the misconfiguration immediately via the capture-failure path. |
| `Restore` throws (execution) | **Count as a failed attempt (retry)** | Consistent with "pipelines run inside the retry boundary"; stale captured data (e.g. deleted user) flows through normal retry → dead-letter. |
| Multiple extractors | Capture and restore are symmetric within generated code; no relative ordering is guaranteed across compiler or package versions | Extractors must be idempotent and must not depend on another extractor running before or after them. |

## 9. Storage impact

- **In-memory provider:** no schema; `JobRecord.Context` flows through automatically.
- **EF Core / Postgres:** one nullable text column on the jobs table
  (`EntityFrameworkCoreJobStorage`, `ImmediateJobsModelBuilderExtensions`), part of the schema. It is
  nullable because most jobs capture nothing, and a durable record may carry no envelope.

## 10. Generator work summary

1. Discover `[UsesJobContext<TExtractor>]` on the job, **including markers**: for each attribute on
   the job, use it if it is `UsesJobContext<>`, otherwise inspect that attribute's own attributes for
   `UsesJobContext<>` (identical to `TransformHandler.GetBehaviorsAttribute` walking
   attribute-of-attribute). Resolve each extractor's `JobContextExtractor<TContext>` base to obtain
   `TContext`. Dedupe by extractor type so a job that reaches the same extractor twice captures once.
2. Emit `JsonTypeInfo` for each distinct `TContext` (reuse `JsonMetadataEmitter`).
3. Emit the `CaptureContext` override on the generated `Scheduler` (inject extractors, key each
   slice by `extractor.Key` at runtime, build envelope, throw on key collision).
4. Emit the restore block at the top of the generated `Invoker` (resolve extractor, read
   `extractor.Key`, deserialize its slice, `Restore`).
5. Emit `TryAddScoped` for referenced extractors; flip scheduler registrations to scoped.
6. Keep the pipeline incremental: extractors are node-local to the job, so
   no new cross-cutting provider is required; the model stays symbol-free and equatable.

## 11. Analyzer diagnostics (new)

- `[UsesJobContext<T>]` where `T` does not derive from `JobContextExtractor`.
- Context type `TContext` not serializable (reuse payload validation messaging).
- Context type contains NodaTime types without the integration package (reuse `IJOB007` shape).

## 12. Example end-to-end

```csharp
// once, reusable
public sealed record UsageContextSnapshot(Guid UserId, string TenantId);
public sealed class UsageContext
{
	public UsageContextSnapshot? Value { get; set; }
}
public sealed class UsageContextExtractor(UsageContext usage)
	: JobContextExtractor<UsageContextSnapshot> { /* Capture/Restore as in §4.2 */ }

builder.Services.AddScoped<UsageContext>();

// applied to any number of jobs
public sealed record InvoicePayload(Guid OrderId);

[Handler, Job, UsesJobContext<UsageContextExtractor>]
public sealed partial class SendInvoiceJob(UsageContext usage)
{
    private ValueTask HandleAsync(InvoicePayload payload, CancellationToken ct)
    {
        // usage.Value was restored from the enqueue-time request.
    }
}

// enqueue from a controller/endpoint (request scope) — captures automatically
await scheduler.EnqueueAsync(new InvoicePayload(orderId));
```

## 13. Decisions

| Question | Decision |
| --- | --- |
| Application model | **Per-job marker + reusable marker attributes** (§4.3). No assembly-wide `[assembly: UsesJobContext<T>]`. |
| Capture failure | **Propagate to caller** — enqueue throws (§8). |
| Restore failure | **Fail the attempt and retry** (§8). |
| Envelope keying | **Stable `Key` property on the extractor** (§4.1) — rename-safe; read at runtime by generated code. |

**To verify during implementation (not open design questions):** confirm that running restore before
`Handler` construction makes the restored scoped services visible to every Immediate.Handlers
behavior (expected, since those run inside `Handler.HandleAsync`) — covered by §14.4 "round trip".

## 14. Testing requirements

All items are required unless marked _(nice-to-have)_. Tests live in the existing projects:
`Immediate.Jobs.Tests` (generator/analyzer), `Immediate.Jobs.FunctionalTests` (runtime),
the Entity Framework Core storage tests in `Immediate.Jobs.FunctionalTests`, and the Native AOT sample.

### 14.1 Generator — snapshot tests (Verify)

- **Single extractor** — the generated `Scheduler` contains the `CaptureContext` override that
  injects the extractor and builds the envelope; the generated `Invoker` contains the restore block
  ahead of the handler/behavior invocation; `JsonTypeInfo` is emitted for the context type; the
  extractor and scheduler are registered `Scoped`.
- **Multiple extractors** — capture and restore include every distinct extractor in the same order
  within an individual generated artifact, and each slice is keyed off `extractor.Key` at runtime.
- **Marker attribute** — a job carrying a custom `[WebJob]` attribute annotated with
  `[UsesJobContext<>]` emits the same capture/restore as the direct form; a job reaching one extractor
  both directly and via a marker captures it once (dedupe).
- **No extractors (regression)** — output is byte-identical to today; no capture/restore code, no
  serialization overhead. Guards against accidental emit on the common case.
- **Context-type shapes** — record, class with bindable constructor, nested/enum members, and a
  NodaTime-bearing context with the integration package referenced, each produce correct
  serialization metadata.
- **Payload + context together** — a job with both a payload and context types emits distinct,
  non-colliding `JsonTypeInfo` and hint names.

### 14.2 Generator — incremental cacheability

- Adding an unrelated syntax tree ⇒ every tracked step reports `Cached`/`Unchanged` (extend the
  existing `VerifyIncrementality` harness with the new steps).
- Editing an extractor referenced by one job ⇒ only that job regenerates; unrelated jobs stay
  cached.
- Editing a context type's shape ⇒ the owning job's serialization regenerates; others unaffected.
- The pipeline model remains symbol-free and equatable (no `ISymbol` captured for extractors).

### 14.3 Analyzer diagnostics

- `[UsesJobContext<T>]` where `T` does not derive from `JobContextExtractor` ⇒ diagnostic.
- Context type not serializable (reuse payload-validation messaging) ⇒ diagnostic.
- Context type contains NodaTime types without the integration package ⇒ diagnostic (`IJOB007`
  shape).
- Each diagnostic has a positive "reports expected diagnostic" test and a negative "valid usage is
  clean" test.

### 14.4 Runtime — functional tests

- **Round trip** — set an ambient/scoped value in an originating scope, enqueue, run the job in the
  worker, and assert the value is visible to the handler and its behaviors during background
  execution.
- **Multiple extractors** — all contexts restore; each extractor is idempotent and independent of
  relative ordering.
- **Recurring/cron** — `Capture` returns `null` (no ambient scope), no envelope is written, and
  the job still executes.
- **Restore failure policy** — a throwing/stale `Restore` behaves per the §8 decision (fail the
  attempt and eventually dead-letter, or run-without-context), asserted end-to-end including retry
  count.
- **Capture failure policy** — a throwing `Capture` behaves per the §8 decision (enqueue throws,
  or succeeds without context).
- **Orphaned slice** — a durable record whose envelope `Key` has no matching registered extractor
  still runs; the slice is skipped and logged.
- **Duplicate key** — two registered extractors sharing a `Key` throw at capture (collision).
- **Rename safety** — renaming an extractor type (keeping its `Key`) still restores a record enqueued
  before the rename.
- **Ordering symmetry** — capture order equals restore order for a multi-extractor job.

### 14.5 Scheduler lifetime (scoped)

- Enqueue succeeds from a request scope **and** from a manually created `IServiceScopeFactory` scope.
- Resolving `Scheduler` from the **root** provider throws — asserted so the scoped registration is
  intentional and covered.
- A singleton consumer that enqueues via `IServiceScopeFactory.CreateScope()` works.

### 14.6 Serialization / AOT

- Context types round-trip through `IJobSerializer` using the generated `JsonTypeInfo` factory
  overloads (no reflection fallback), mirroring payload serialization tests.
- The Native AOT sample includes at least one job using an extractor, compiled in CI, to guard the
  reflection-free/trimming-safe claim for the context path.

### 14.7 Storage

- **In-memory** — `JobRecord.Context` flows through capture → persist → restore unchanged.
- **EF Core / Postgres** — the nullable `Context` column persists and round-trips; a row with
  `Context = null` deserializes to "no context" and runs.

### 14.8 Durable-record robustness

- A record with no `Context` (a job that captures nothing) runs normally.
- A record carrying a `Context` envelope runs even when the referenced extractor is unregistered or
  its type was renamed (see §14.4 "orphaned slice" / "rename safety").

## 15. Docs & follow-through

- Update `spec.md` (§2.6 area) and the readme with the extractor abstraction, the
  `[UsesJobContext<>]` marker (direct + reusable-marker forms), and the scoped-scheduler DI note.
- The scoped-scheduler DI expectation (singleton enqueuers must open a scope) is documented as
  standard guidance, alongside the existing schedulers documentation.

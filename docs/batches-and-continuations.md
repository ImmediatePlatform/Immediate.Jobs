# Immediate.Jobs — Batches & Continuations

**Atomic batch creation and DAG-shaped job workflows, reflection-free, source-generated, AOT-safe.**

Status: Design draft v0.1 · Date: 2026-07-21 · Target: .NET 8+ · Package: `Immediate.Jobs` (core)

> This is the v2 chapter for the two features listed as non-goals in [`spec.md §1`](../spec.md):
> *job continuations/chains* and *batch jobs*. It builds directly on the existing `IJobStorage`
> seam, generated per-job `Scheduler` types, opaque string job IDs, and the single-server /
> distributed / in-memory topologies described there.

---

## 1. Positioning

Hangfire Pro offers two paid capabilities: **atomic batch creation** (a group of jobs that all
appear or none do) and **continuations** (job→job chains and batch→batch continuations). Its API
has three warts we deliberately remove:

1. **Static entry points** — `BatchJob.StartNew(...)` / `BatchJob.ContinueBatchWith(id, ...)` don't
   compose with DI and read nothing like the rest of the app.
2. **Expression-tree jobs** — `x.Enqueue(() => SendEmail(i))` serializes a lambda and
   reflection-invokes it. This is exactly what Immediate.Jobs exists to eliminate.
3. **Raw string-ID threading** — `var id1 = ...; ContinueJobWith(id1, ...)` — loose IDs passed by
   hand, no navigation, no compile-time safety.

Immediate.Jobs replaces all three by keeping **the generated scheduler as the subject** — exactly as
`sendEmail.EnqueueAsync(payload)` reads today. The job you are scheduling is always the receiver:
`sendEmail.AddToBatch(batch, payload)` adds it to an atomic batch, and `two.ScheduleAfterAsync(oneBJob,
payload)` schedules it after a parent. **Typed handles** (`JobHandle`, `BatchHandle`) are the
currency passed between schedulers to wire dependencies — a `[a, b, c]`
collection of handles expresses a fan-in. Nothing but data is
ever persisted — no closures, no expression trees, no `MethodInfo`, and therefore **no reflective
dispatch to add**. Continuations go beyond Hangfire Pro to a full **DAG**: fan-out (branch), fan-in
(join), diamonds, and continuations-of-continuations — with or without a batch.

**Non-goals for this chapter:** result-passing between parent and child (a continuation observes a
parent's *outcome*, not its return value), long-running human-in-the-loop workflow suspension, and
compensation/saga rollback semantics. These remain candidates for a later chapter.

---

## 2. Why nothing stores a lambda

The single most important property: **the batch builder body is a scope delimiter, not a stored
artifact.** It runs once, synchronously, at enqueue time; it buffers `JobRecord`s; the buffer is
committed atomically; the delegate is discarded. What lands in storage is byte-for-byte what a
normal `scheduler.EnqueueAsync(payload)` writes today — a `JobName` string plus a JSON `Payload` — and
dispatch resolves the job through the same generated `IJobInvoker`. No reflection is introduced.

Continuations preserve this property. `child.ScheduleAfterAsync(parent, payload)` does **not** defer code.
It writes a real, fully-serialized `JobRecord` for the child *now*, parked in a new
`AwaitingContinuation` state, plus one or more **edges** describing what it waits for. When a parent
reaches a terminal state, storage flips the child to `Pending` (or `Cancelled`). The only new
durable data is **edges and counters** — never behavior.

---

## 3. Developer experience

### 3.1 Handles are the currency (v1 change)

Continuations pass a job's identity to another scheduler, so `EnqueueAsync`/`ScheduleAsync`/`ScheduleAtAsync` return
a **`JobHandle`** — an opaque value carrying the job's `.Id` — instead of a bare `string`:

```csharp
JobHandle h = await oneB.ScheduleAtAsync(new(), runAt);   // was: string id
string id = h.Id;                                    // still available when you only want the id
```

This is a small change to the existing v1 scheduler surface (return type `ValueTask<string>` →
`ValueTask<JobHandle>`). The handle is what a downstream scheduler consumes via `ScheduleAfterAsync` (§3.3).
Because job IDs are **client-generated before storage** (`IIdGenerator.CreateId(IdKind.Job)`), a handle
carries a real ID immediately — even for a job still buffered in an uncommitted batch — which is what
makes intra-batch continuations resolvable before commit.

### 3.2 Atomic batch — the primary API

`IJobBatchScheduler` is an injected, scoped service (resolve it from a request/DI scope exactly like
a generated `Scheduler`). `Begin()` opens an in-memory buffer; you add work by calling each job's
generated **`AddToBatch`** — the job is the receiver, exactly as with `EnqueueAsync`; `CommitAsync` flushes
the buffer in one atomic unit. Disposing without committing rolls the buffer back — giving Hangfire's
"exception before commit ⇒ nothing enqueued" guarantee mechanically.

```csharp
public sealed class CampaignService(
    IJobBatchScheduler batches,
    SendEmail.Scheduler sendEmail)
{
    public async ValueTask SendCampaignAsync(IReadOnlyList<Guid> recipients, CancellationToken ct)
    {
        await using var batch = batches.Begin();

        foreach (var id in recipients)
			sendEmail.AddToBatch(batch, new(id));   // job is the subject, like EnqueueAsync

        await batch.CommitAsync(ct);                       // single atomic flush — all rows, or none
    }
}
```

`AddToBatch` is generated per scheduler (like `EnqueueAsync`), so it is typed, F12-navigable, and needs no
generic `Add<T>(scheduler, payload)` seam. If storage is unavailable mid-loop, **nothing** was
written (the buffer only touches storage at commit), so retrying the whole method cannot double-send —
the "1000 emails" scenario, solved without duplicate-tracking bookkeeping.

`AddToBatch` mirrors the scheduler timing surface — immediate, delayed, and absolute-time:

```csharp
sendEmail.AddToBatch(batch, new(id));
sendEmail.AddToBatch(batch, new(id), delay: TimeSpan.FromMinutes(5));
sendEmail.AddToBatchAt(batch, new(id), runAt);
```

For callers who prefer a block, `RunAsync` is a thin wrapper over `Begin` → body → `CommitAsync`
(rollback on throw). It is sugar, not the canonical form:

```csharp
var batch = await batches.RunAsync(b =>
{
    foreach (var id in recipients)
		sendEmail.AddToBatch(b, new(id));
	return ValueTask.CompletedTask;
}, ct);
```

> **Ambient auto-join is intentionally not offered.** An `AsyncLocal` batch that silently captures
> ordinary `sendEmail.EnqueueAsync` calls is terse but unsafe across `await`/`Task.WhenAll` and hides
> where durable writes happen. `sendEmail.AddToBatch(batch, ...)` is the one obvious way to enqueue
> into a batch.

### 3.3 Continuations (chains & branches)

Every scheduler has generated **`ScheduleAfterAsync(parent, payload)`**: it schedules *its own* job to run
after `parent` — the job being scheduled is the receiver, the parent handle is the argument. It works
identically inside a batch or standalone.

Calling `ScheduleAfterAsync` with the same parent twice **fans out**:

```csharp
await using var batch = batches.Begin();

var built = buildArtifact.AddToBatch(batch, new(commit));

await deployStaging.ScheduleAfterAsync(built, new());   // branch A — runs after `built`
await publishDocs.ScheduleAfterAsync(built, new());     // branch B — runs after `built`

await batch.CommitAsync(ct);
```

Continuations chain because `ScheduleAfterAsync` returns the new job's `JobHandle`:

```csharp
var h1 = step1.AddToBatch(batch, new(), delay: TimeSpan.FromSeconds(1));
var h2 = await step2.ScheduleAfterAsync(h1, new());
await step3.ScheduleAfterAsync(h2, new());              // step1 → step2 → step3
```

**No batch required.** A `ScheduleAfterAsync` whose parent is not batch-scoped is its own durable write — a
plain job→job continuation:

```csharp
var oneBJob = await oneB.ScheduleAtAsync(new(), runAt);
await two.ScheduleAfterAsync(oneBJob, new());           // `two` runs after `oneB`; no batch involved
```

> A standalone `ScheduleAfterAsync` and its parent are two separate writes, so the parent may already be
> terminal by the time the child is created. In that case the continuation is **evaluated
> immediately** (released or cancelled per its trigger) rather than waiting forever. Inside a batch
> the two are written atomically, so this race cannot occur.

### 3.4 Fan-in / join

Pass several parents as a collection to `ScheduleAfterAsync`; the child waits for **all** of them (a
many-parent edge set), fires on whatever node is free, and needs no in-process waiting. A collection
expression binds allocation-free to the `ReadOnlySpan<JobHandle>` overload:

```csharp
var a = deployRegionA.AddToBatch(batch, new());
var b = deployRegionB.AddToBatch(batch, new());
var c = deployRegionC.AddToBatch(batch, new());

await runSmokeTests.ScheduleAfterAsync([a, b, c], new(), on: ContinuationTrigger.Success);
```

For a dynamic number of parents, pass a `JobHandle[]` directly, or `CollectionsMarshal.AsSpan(list)`
for a `List<JobHandle>` built in a loop.

Diamonds compose naturally, since `ScheduleAfterAsync` yields a `JobHandle` you can branch or join again:

```
        ┌─> deployA ─┐
build ──┼─> deployB ─┼─> smokeTests ──> announce
        └─> deployC ─┘
```

### 3.5 Batch continuations

A committed batch yields a `BatchHandle`. A single job continues after the **entire batch** by taking
that handle as its parent:

```csharp
var emailBatch = await batches.RunAsync(b =>
{
    foreach (var id in recipients)
		sendEmail.AddToBatch(b, new(id));
	return ValueTask.CompletedTask;
}, ct);

await notifyAdministrator.ScheduleAfterAsync(emailBatch, new());   // runs once the whole batch is done
```

To run an **atomic group** after a batch, open the follow-up batch with `after:` — every root member
it adds also waits on the prior batch:

```csharp
await using var wrapUp = batches.Begin(after: emailBatch, on: ContinuationTrigger.Success);
markCampaignFinished.AddToBatch(wrapUp, new(campaignId));
notifyAdministrator.AddToBatch(wrapUp, new());
await wrapUp.CommitAsync(ct);
```

### 3.6 Mid-job scheduling (dynamic expansion)

A running batch member often needs to expand the workflow *at execution time*, because the data it
needs only exists once it runs — e.g. `ProcessOrder` loads the order, then wants to send an email
whose contents it just computed, before post-processing runs. The member schedules new work relative
to **itself**, using its own `JobDetails` (from `IJobRequest.JobDetails`, attached by the worker) as
the explicit "I am running inside this job" token — no ambient state.

The two existing verbs keep their meaning, now pointed at the current job `C`:

- **`ScheduleAfter(JobDetails, payload, options)`** — *gated*: `C → J`, so J runs after C. Its
  creation is **buffered until C completes successfully**, so a C that fails and retries does not
  double-schedule J.
- **`AddToBatchAsync(JobDetails, payload, options)`** — *concurrent*: J joins C's batch as an unordered
  member and is written **immediately**, so it can run alongside the tail of C.

```csharp
[Handler, Job]
public sealed partial class ProcessOrder(SendEmail.Scheduler sendEmail)
{
    public sealed record Command(Guid OrderId) : IJobRequest
    {
        public JobDetails? JobDetails { get; set; }
    }

    private async ValueTask HandleAsync(Command command, CancellationToken ct)
    {
        var order = await LoadOrderData(command.OrderId, ct);

        // Splice the email before post-processing: everything that waited on this job now
        // waits on {this job, email}.
		sendEmail.ScheduleAfter(command.JobDetails!,
            new(order.CustomerEmail, order.Summary),
			options: ContinuationOptions.BeforeContinuations);
    }
}
```

`ContinuationOptions` chooses how C's existing waiters `X` relate to the new job:

| Option                          | J in batch? | C's waiters `X` wait for J?                 |
| ------------------------------- | ----------- | ------------------------------------------- |
| `Detached`                      | no          | no — J is untracked by the batch            |
| `BesideContinuations`           | yes         | no — J runs as a parallel branch            |
| `BeforeContinuations` (default) | yes         | yes — `X` waits on `{C, J}` (additive splice) |

`BeforeContinuations` performs an **additive** splice: for every existing edge `C → X` it *adds* an
edge `J → X` while keeping `C → X`, so each former waiter now depends on **both**. This is correct
regardless of whether J finishes before or after C — `X` cannot start until both are satisfied.

> **The verb also picks durability — by design.** `ScheduleAfter(JobDetails, …)` is gated and
> buffered, so it is retry-safe: if C fails and retries, J is never double-created.
> `AddToBatchAsync(JobDetails, …)` is concurrent and written immediately, so **J can fire even if C then
> fails and retries** — it must be idempotent, like any at-least-once job. Asking for work to run
> *now* means it runs now.

> **`AddToBatchAsync(JobDetails, Detached)` is a contradiction** ("add to the batch" + "detached from the
> batch") and is rejected — by the analyzer (`IJOB020`) at compile time when the option is a constant,
> and by a runtime guard otherwise. `Detached` is meaningful only on `ScheduleAfter(JobDetails, …)`.

> **The batch-tracking paths require the current job to be in a batch.** Whether the executing job is
> a batch member is only known at run time, so this is a **runtime** guard, not an analyzer check:
> when the current job has **no batch**, `AddToBatchAsync(JobDetails, …)` and
> `ScheduleAfter(JobDetails, …)` with `BesideContinuations` or `BeforeContinuations` **throw** — there
> is no batch to add to, track, or splice into. The only valid mid-job call for a batch-less job is
> `ScheduleAfter(JobDetails, …, Detached)`, i.e. a plain standalone continuation after it.

### 3.7 Observing completion

Batches are durable and fire-and-forget. To act when one finishes, attach a continuation (§3.4/§3.5):
it is the workflow primitive, survives request completion / restart / failover, and runs on whatever
node is free. To *read* a batch's progress or outcome, use the read API (§8) or watch it in the
dashboard (§7).

### 3.8 Continuation triggers

| Trigger                 | Fires when …                                               | When the condition is not met                    |
| ----------------------- | ---------------------------------------------------------- | ------------------------------------------------ |
| `Success` **(default)** | every parent reached `Succeeded`                           | child is **cancelled**, cascading to its subtree |
| `Failure`               | every parent is terminal and at least one reached `Failed` | child is **cancelled**, cascading to its subtree |
| `Complete`              | every parent reached any terminal state                    | always runs                                      |

`Success` is the safe default: a broken upstream step never silently runs downstream work.
`Failure` is the failure-handler trigger. For a `BatchHandle`, it fires only when the aggregate
batch state is `Failed`; cancellation alone does not count as failure. For fan-in over several
`JobHandle`s, it waits until every parent settles and fires when at least one parent failed.
`Complete` is the `finally` trigger for cleanup or notification work that runs regardless of outcome.

```csharp
var batch = await batches.RunAsync(b =>
{
	importCustomers.AddToBatch(b, new(importId));
	updateSearchIndex.AddToBatch(b, new(importId));
	return ValueTask.CompletedTask;
}, ct);

await handleImportFailure.ScheduleAfterAsync(
    batch,
    new(importId),
    on: ContinuationTrigger.Failure,
    cancellationToken: ct);
```

---

## 4. Storage model

### 4.1 New durable state

A new lifecycle state parks pre-created continuation rows, plus one reserved state:

```
Scheduled → Pending → Active → Succeeded | Failed | Cancelled
                 ↑
   AwaitingContinuation   (created, ineligible until every incoming edge is satisfied)
   AwaitingParameters     (reserved; ineligible until required inputs are supplied → Pending)
```

`AwaitingParameters` is **reserved and unused by this spec.** Nothing in the current design produces
or consumes it — it is defined now so the `JobState` enum, storage columns, and provider queries
account for it up front, letting a future human-in-the-loop / deferred-input capability (a job created
without complete parameters, released to `Pending` once they are supplied) land **without a state-enum
or schema migration**. Providers should treat an `AwaitingParameters` row as non-acquirable, exactly
like `AwaitingContinuation`.

### 4.2 Schema deltas

**`immediate_job_batches`**

| Column         | Type              | Notes                                             |
| -------------- | ----------------- | ------------------------------------------------- |
| `Id`           | string (PK)       | Opaque, client-generated                          |
| `CreatedAt`      | timestamptz     |                                                   |
| `TotalJobs`      | int             | Members committed with the batch                  |
| `PendingCount`   | int             | Non-terminal members; decremented as members reach terminal state |
| `SucceededCount` | int             | Members that succeeded (maintained in §4.4)       |
| `FailedCount`    | int             | Members that failed                               |
| `CancelledCount` | int             | Members cancelled (incl. cascade)                 |
| `StartedAt`      | timestamptz?    | Set when the first member goes `Active`           |
| `CompletedAt`    | timestamptz?    | Set when `State` becomes terminal                 |
| `State`          | enum            | `Executing | Succeeded | Failed | Cancelled`      |

The three per-state counters (plus `PendingCount`) make `GetStatusAsync` (§8) a single-row read; they
are incremented in the same completion transaction that already decrements `PendingCount`.

**`immediate_job_continuations`** (edge table — expresses arbitrary DAGs)

| Column            | Type          | Notes                                                |
| ----------------- | ------------- | ---------------------------------------------------- |
| `ChildJobId`      | string (FK)   | The waiting invocation                               |
| `ParentJobId`     | string? (FK)  | Set for job→job edges                                |
| `ParentBatchId`   | string? (FK)  | Set for batch→job edges                              |
| `Trigger`         | enum          | `Success | Failure | Complete`                       |

Primary key `(ChildJobId, ParentJobId, ParentBatchId)`. Exactly one parent column is non-null per row.

**`JobRecord` additions**

| Column                   | Type     | Notes                                                     |
| ------------------------ | -------- | --------------------------------------------------------- |
| `BatchId`                | string?  | Membership in an atomic batch                             |
| `RemainingDependencies`  | int      | Count of unsatisfied incoming edges; `0` ⇒ releasable     |
| `FailedDependencies`     | int      | Settled incoming edges whose parent reached `Failed`      |

The dependency counters are maintained transactionally alongside the edge table so the hot
"release?" check is a single indexed read, never a join-and-count. `FailedDependencies` lets a
`Failure` continuation wait for every parent and then decide whether any parent actually failed.

### 4.3 Atomic commit

`CommitAsync` maps to a new storage call:

```csharp
ValueTask EnqueueBatchAsync(
    JobBatchRecord batch,
    IReadOnlyList<JobRecord> jobs,
    IReadOnlyList<JobContinuationEdge> edges,
    CancellationToken cancellationToken = default);
```

Members with unsatisfied edges are inserted as `AwaitingContinuation` with `RemainingDependencies`
preset; members with no edges are inserted `Pending`/`Scheduled` as usual. The batch row, all job
rows, and all edge rows are written in **one atomic unit**. No member is ever acquirable until the
unit commits, so a worker cannot observe a partial batch.

### 4.4 Completion & release

`CompleteAsync` / `FailAsync` gain post-terminal responsibilities, executed **in the same
transaction** as the state change:

1. If the job belongs to a batch, decrement `PendingCount` and increment the matching
   `Succeeded`/`Failed`/`CancelledCount` (and set `StartedAt` on the first `Active` transition); if
   `PendingCount` reaches `0`, finalize the batch's `State`, stamp `CompletedAt`, and treat the batch
   as a terminal parent for batch→job edges.
2. For each outgoing edge from this job (or batch), decrement the child's `RemainingDependencies`
   and increment `FailedDependencies` when the parent failed.
   - **`Success`:** a non-successful parent immediately moves the child to `Cancelled`; otherwise,
     release it when `RemainingDependencies` reaches `0`.
   - **`Failure`:** wait until `RemainingDependencies` reaches `0`, then release if
     `FailedDependencies > 0`; otherwise move the child to `Cancelled`.
   - **`Complete`:** release when `RemainingDependencies` reaches `0`, regardless of outcomes.
   Cancellation **recurses** through outgoing edges, cascading the resulting outcome through the
   subtree.

Release and cancellation are set-based within the provider (EF Core: `ExecuteUpdate` over the
affected edge/child set) so a wide fan-out doesn't degrade to N round-trips.

### 4.5 Mid-job scheduling mechanics

Mid-job scheduling (§3.6) reuses the batch/edge machinery; it adds two execution-time paths keyed off
the current job `C`'s `JobDetails`:

- **`ScheduleAfter(JobDetails, …)` — gated, buffered.** The new job `J` is accumulated in a
  per-execution buffer and flushed **in the same transaction as `C`'s successful `CompleteAsync`**.
  The flush inserts `J` (as `AwaitingContinuation` with edge `C → J`), increments the batch's
  `TotalJobs`/`PendingCount`, and — because it runs before `C`'s own completion is finalized — closes
  the completion race (the batch can never finalize between the add and `C`'s completion). If `C`
  fails, the buffer is discarded, so a retry never double-creates `J`.
- **`AddToBatchAsync(JobDetails, …)` — concurrent, immediate.** `J` is written in its own transaction the
  moment the call is made: inserted `Pending` (no `C → J` edge), incrementing `TotalJobs`/`PendingCount`
  so the batch stays open for it. It may run before `C` finishes; if `C` later fails and retries, `J`
  has already fired (idempotency is the caller's responsibility, as documented in §3.6).

For `ContinuationOptions.BeforeContinuations`, the splice is **additive**: for each existing edge
`C → X`, an edge `J → X` is inserted (and `X`'s `RemainingDependencies` incremented) while `C → X` is
left intact — never a re-parent. `BesideContinuations` adds no `J → X` edges; `Detached` additionally
skips batch membership (and is rejected for `AddToBatchAsync`, see `IJOB020`).

Both batch-tracking paths first check `C`'s `JobDetails` for batch membership: if `C` has no batch,
`AddToBatchAsync(JobDetails, …)` and `ScheduleAfter(JobDetails, …)` with `BesideContinuations`/
`BeforeContinuations` throw before any write (§3.6) — only `ScheduleAfter(JobDetails, …, Detached)`
proceeds, as an ordinary standalone continuation.

### 4.6 Topology behavior

| Provider              | Atomicity of commit                    | Completion/release                                   |
| --------------------- | -------------------------------------- | ---------------------------------------------------- |
| EF Core (distributed) | Real DB transaction                    | In the same transaction as `Complete`/`Fail`         |
| Single-server         | Write-through transaction, then publish to the in-process queue | Memory authority mirrors edge decrements through storage |
| In-memory (dev)       | All-or-none under one lock; **not durable** | Best-effort, single-node; documented as dev-only     |

The in-memory dev provider supports the full API (per the "all three, best-effort" decision) so
tests and samples exercise batches without a database, with the standard non-durability caveat.

### 4.7 Retention

A batch is retained and purged **as an atom**, not member-by-member. Individual job retention (24h
succeeded / 7d failed) does not govern batch members; the whole DAG — batch row, every member, and
every edge — is kept for a window keyed off the **batch's** terminal state, then purged in one unit:

| Option                     | Default | Applies when the batch's `State` is …            |
| -------------------------- | ------- | ------------------------------------------------ |
| `BatchSucceededRetention`  | 24h     | `Succeeded`                                       |
| `BatchFailedRetention`     | 7d      | `Failed` or `Cancelled`                           |

```csharp
builder.Services.AddImmediateJobs(o =>
{
    o.BatchSucceededRetention = TimeSpan.FromHours(24); // defaults mirror job retention
    o.BatchFailedRetention = TimeSpan.FromDays(7);
});
```

Retaining the batch as a unit is what keeps the workflow view coherent: while you debug a failed
step, the *succeeded* steps of the same DAG are still present, aggregate counts stay correct, and the
graph never shows holes from members that aged out early. `PurgeAsync` gains a batch-aware pass that
deletes eligible batches and their members/edges together, and never purges a member whose batch is
still within its window.

---

## 5. Execution & coordination

- **No new worker path.** Released continuations are ordinary `Pending` rows acquired by the
  existing `AcquireDueJobsAsync` loop and dispatched through the same generated invoker. Batches and
  continuations are purely a *creation-and-release* concern.
- **Distributed safety.** Counter decrements and edge evaluation run inside the same optimistic-
  concurrency transaction as the terminal state change, so concurrent completions on different nodes
  can't double-release or lose a decrement. A released child is claimable by any node.
- **Crash mid-cascade.** Because release/cancel is transactional with completion, a node dying
  leaves either the pre- or post-completion state — never a half-released fan-out. Orphan sweeps
  (already used for expired leases) also reconcile any `AwaitingContinuation` row whose every parent
  is terminal but whose release was interrupted.
- **Recurring × batches.** A recurring job may open a batch in its handler; the batch is an ordinary
  atomic unit with no special recurring coupling.

---

## 6. Compile-time & runtime diagnostics

`AddToBatch`/`ScheduleAfterAsync` are generated per scheduler and strongly typed against the job's
`IJobRequest` payload, so wrong-payload and unserializable-payload errors reuse the existing
`IJOB003`/`IJOB015` analyzers. New diagnostics:

| ID        | Meaning                                                                        |
| --------- | ------------------------------------------------------------------------------ |
| `IJOB016` | Batch committed empty (no `AddToBatch`) — likely a mistake                     |
| `IJOB017` | `ScheduleAfterAsync` mixes handles from different, unrelated batches                |
| `IJOB018` | Continuation would create a dependency cycle (detectable when handles are known at build of the batch) |
| `IJOB019` | `JobHandle` used after its batch was committed or disposed                     |
| `IJOB020` | `AddToBatchAsync(JobDetails, …, Detached)` — contradictory (§3.6); `Detached` is valid only on `ScheduleAfter(JobDetails, …)` |

`IJOB018` cycle detection is best-effort at the buffer level (the buffer holds the intended edge set
before commit); a runtime guard in `EnqueueBatchAsync` remains the backstop. `IJOB020` fires at
compile time when `options` is a constant; a runtime guard rejects the combination otherwise.

---

## 7. Dashboard

Batches get a first-class, workflow-style presentation — the DAG is the point of the feature, so the
dashboard renders it as a graph rather than a flat job list.

### 7.1 Batches list

A table of batches with live progress, one row each:

- A compact **progress bar** segmented by state — succeeded / failed / cancelled / running / waiting
  of `TotalJobs` — updated over SSE.
- Batch `State` badge (`Executing`, `Succeeded`, `Failed`, `Cancelled`), member count, age, and the
  originating job/queue.
- Filter by state and search; click through to the workflow viewer.

### 7.2 Workflow viewer (per batch)

The centrepiece: an auto-laid-out **left-to-right DAG** of the batch's members and continuation
edges — a pipeline/graph canvas, not a list.

- **Nodes** are jobs, colour-coded by state (waiting · scheduled · running · succeeded · failed ·
  cancelled), labelled with the job name; a running node shows a subtle pulse, a failed node a
  badge. Selecting a node opens the existing job-detail panel (payload, attempts, errors, timings)
  in a side drawer without leaving the graph.
- **Edges** are continuation dependencies, drawn `parent → child` and styled by trigger
  (`Success` solid, `Failure` red/dotted, `Complete` dashed). Fan-out, fan-in (join), and diamonds render as their
  true shape; a **violated `Success` edge and the subtree it cancelled are dimmed/struck** so a
  cascade is visible at a glance.
- **Layered layout** (topological rank) with pan/zoom and a mini-map for large fan-outs; nodes with
  hundreds of siblings collapse into a **"×N same job"** group that expands on demand, so a
  1000-email batch stays legible.
- Batch-continuation targets (§3.5) render as a downstream cluster linked from the whole batch, so
  chained batches read as one continuous workflow.
- Live: node/edge states stream over SSE; the graph animates transitions (waiting → running →
  succeeded, or the cascade dimming) in place.

### 7.3 Actions

- **Retry job** — re-runs *only* the selected failed member. Cascade-cancel is terminal, so
  downstream `Cancelled` nodes stay cancelled (see §4.4).
- **Retry from here** — re-materializes the failed member **and its cancelled descendants** as a
  fresh sub-DAG (new IDs, new edges) attached to the same batch, rather than resurrecting terminal
  rows. This is the "resume the workflow" action; the viewer shows the re-materialized branch as a
  new generation grafted onto the graph, with the original cancelled subtree preserved for history.
- **Cancel batch** — cascades to all non-terminal members.
- **Delete batch** — removes a terminal batch as a unit (members + edges), consistent with §4.7.

### 7.4 Monitoring API

The JSON + SSE surface gains, alongside the existing job resources:

- `GET batches` — paged list with progress aggregates.
- `GET batch/{id}` — batch header, counts, state.
- `GET batch/{id}/graph` — nodes + edges for the workflow viewer (stable shape, usable without the
  SPA).
- `POST batch/{id}/cancel`, `POST job/{id}/retry`, `POST job/{id}/retry-subtree`,
  `DELETE batch/{id}`.
- SSE `batch/{id}/stream` — per-node/edge state deltas.

---

## 8. Programmatic read API

For code that needs to observe batches in-process — a custom endpoint reporting progress, a worker
deciding what to do next — two injectable, read-only services return plain records. They carry **no
serialization opinion**: no shipped `JsonSerializerContext`, no wire contract. Map or serialize the
results however the consumer likes. (The dashboard's own JSON+SSE surface in §7.4 is separate and
keeps its own wire DTOs; it may read through these services internally.)

```csharp
public interface IJobBatchMonitor           // batch reads
{
    ValueTask<BatchStatus?> GetStatusAsync(string batchId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<BatchMemberStatus>> QueryMembersAsync(
        string batchId, BatchMemberQuery query, CancellationToken ct = default);
    ValueTask<BatchGraph?> GetGraphAsync(string batchId, CancellationToken ct = default);
}

public interface IJobMonitor                // single-job reads (not batch-specific)
{
    ValueTask<JobStatus?> GetJobAsync(string jobId, CancellationToken ct = default);
}
```

Result records:

```csharp
public sealed record BatchStatus(
    string Id, BatchState State,
    int Total, int Succeeded, int Failed, int Cancelled,
    int Remaining,                          // non-terminal (running + waiting); == PendingCount
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    double FractionSettled);                // (Total - Remaining) / Total

public sealed record BatchMemberQuery { public JobState? State; public int Skip; public int Take = 100; }

public sealed record BatchMemberStatus(
    string JobId, string JobName, string QueueName, JobState State,
    int Attempt, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? LastError);

public sealed record BatchGraph(string BatchId,
    IReadOnlyList<BatchGraphNode> Nodes, IReadOnlyList<BatchGraphEdge> Edges);
public sealed record BatchGraphNode(string JobId, string JobName, JobState State);
public sealed record BatchGraphEdge(
    string ChildJobId, string? ParentJobId, string? ParentBatchId, ContinuationTrigger Trigger);

public sealed record JobStatus(
    string JobId, string JobName, string QueueName, JobState State,
    int Attempt, int MaxAttempts,
    DateTimeOffset CreatedAt, DateTimeOffset DueAt, DateTimeOffset? CompletedAt,
    string? LastError, string? BatchId,
    IReadOnlyList<BatchGraphEdge> DependsOn); // its incoming continuation edges

public enum BatchState { Executing, Succeeded, Failed, Cancelled }
```

- **`GetStatusAsync`** is a single-row read backed by the batch-row counters (§4.2), so it is cheap
  enough to poll at a per-second cadence for a live progress bar. `Remaining` collapses running and
  waiting; the exact split is a `QueryMembersAsync(new { State = ... })` call when a caller needs it.
- **`QueryMembersAsync`** pages the batch's members, optionally filtered by state — e.g. list only the
  `Failed` members to surface errors.
- **`GetGraphAsync`** returns nodes + edges for a caller building its own DAG rendering; it is the same
  data the dashboard's workflow viewer (§7.2) consumes.
- **`GetJobAsync`** returns one job with its batch membership and incoming continuation edges; returns
  `null` for an unknown or purged id (as do the batch reads).

Each maps to a storage call over the batch/member/edge tables already defined — no new tables.

---

## 9. Testing

`Immediate.Jobs.Testing` additions:

- `JobTestHarness.Batches` — build and commit batches against the in-memory provider under
  `FakeTimeProvider`.
- Assertions: `AssertBatchCommittedAtomicallyAsync`, `AssertContinuationReleasedAfterAsync(parent)`,
  `AssertCascadeCancelledAsync(subtree)`.
- Advance-and-drain helpers already resolve delayed members; continuation release is deterministic
  because it is driven by completion, not wall-clock.

---

## 10. Public surface summary

Methods generated on every job's `Scheduler` (shown for a job with a `Payload` request; each is a
concrete generated member, not a shared interface):

```csharp
// e.g. SendEmail.Scheduler
{
    // v1 enqueue/schedule — now return JobHandle instead of string
    ValueTask<JobHandle> EnqueueAsync(Payload payload, CancellationToken ct = default);
    ValueTask<JobHandle> ScheduleAsync(Payload payload, TimeSpan delay, CancellationToken ct = default);
    ValueTask<JobHandle> ScheduleAtAsync(Payload payload, DateTimeOffset runAt, CancellationToken ct = default);

    // batch membership (root member, no dependency)
	JobHandle AddToBatch(IJobBatch batch, Payload payload, TimeSpan? delay = null);
	JobHandle AddToBatchAt(IJobBatch batch, Payload payload, DateTimeOffset runAt);

    // continuations — this job runs after the parent(s); works in a batch or standalone
    ValueTask<JobHandle> ScheduleAfterAsync(JobHandle parent, Payload payload,
        ContinuationTrigger on = ContinuationTrigger.Success,
        TimeSpan? delay = null, CancellationToken ct = default);
    ValueTask<JobHandle> ScheduleAfterAsync(ReadOnlySpan<JobHandle> parents, Payload payload,   // fan-in
        ContinuationTrigger on = ContinuationTrigger.Success,
        TimeSpan? delay = null, CancellationToken ct = default);
    ValueTask<JobHandle> ScheduleAfterAsync(BatchHandle parentBatch, Payload payload,
        ContinuationTrigger on = ContinuationTrigger.Success,
        TimeSpan? delay = null, CancellationToken ct = default);

    // mid-job scheduling (§3.6) — relative to the currently-executing job via its JobDetails
	JobHandle ScheduleAfter(JobDetails current, Payload payload,   // gated, buffered, retry-safe
		ContinuationOptions options = ContinuationOptions.BeforeContinuations);
    ValueTask<JobHandle> AddToBatchAsync(JobDetails current, Payload payload,      // concurrent, immediate
        ContinuationOptions options = ContinuationOptions.BeforeContinuations, CancellationToken ct = default);
}
```

Batch and handle types:

```csharp
public interface IJobBatchScheduler
{
    IJobBatch Begin();
    IJobBatch Begin(BatchHandle after, ContinuationTrigger on = ContinuationTrigger.Success);
    ValueTask<BatchHandle> RunAsync(Func<IJobBatch, ValueTask> build, CancellationToken ct = default);
}

public interface IJobBatch : IAsyncDisposable
{
    ValueTask<BatchHandle> CommitAsync(CancellationToken ct = default);
}

public readonly struct JobHandle          // carries the job Id (+ owning batch, if any)
{
    string Id { get; }
}

public sealed class BatchHandle           // carries the batch Id
{
    string Id { get; }
}

public enum ContinuationTrigger { Success, Failure, Complete }

// how a mid-job (§3.6) scheduled job relates to the current job's existing waiters
public enum ContinuationOptions
{
	Detached,             // not tracked by the batch; waiters unaffected (ScheduleAfter only)
    BesideContinuations,  // batch member; runs as a parallel branch, waiters do not wait for it
    BeforeContinuations,  // batch member; waiters wait on {current, new} (additive splice)
}
```

> `AddToBatch`/`ScheduleAfterAsync` are generated on each job's scheduler exactly like `EnqueueAsync`, so the
> job you are scheduling is always the receiver — F12 navigates straight to the job class, and there
> is no generic `Add<T>(scheduler, payload)` seam.

---

## 11. Milestones

| Milestone   | Scope                                                                                             |
| ----------- | ------------------------------------------------------------------------------------------------- |
| **B0**      | v1 scheduler change: `EnqueueAsync`/`ScheduleAsync`/`ScheduleAtAsync` return `JobHandle` instead of `string`     |
| **B1**      | `IJobBatchScheduler` + `Begin`/`AddToBatch`/`CommitAsync`, atomic `EnqueueBatchAsync`, in-memory + EF Core |
| **B2**      | `ScheduleAfterAsync` continuations (chains + fan-out), `AwaitingContinuation`, transactional release   |
| **B3**      | Fan-in joins (`ScheduleAfterAsync([...])`), cascade-cancel, cycle detection, batch continuations (`Begin(after:)`) |
| **B4**      | Mid-job scheduling (§3.6) — `ScheduleAfter`/`AddToBatchAsync(JobDetails, …)`, `ContinuationOptions`, execution buffer + flush, `IJOB020` |
| **B5**      | Read API (§8) — `IJobBatchMonitor` + `IJobMonitor`, batch-row counters, backing storage reads    |
| **B6**      | Batch-atom retention (§4.7), dashboard batches list + workflow viewer + retry/cancel/delete, testing helpers, docs |
| **B7** *(v1.x)* | **Retry from here** subtree re-materialization (§7.3) and its `job/{id}/retry-subtree` endpoint |

---

## 12. Resolved decisions

- **Retention (§4.7):** a batch is retained and purged as an atom, keyed off the *batch's* terminal
  state (`BatchSucceededRetention` 24h / `BatchFailedRetention` 7d), not member-by-member — so a
  failed DAG can be inspected whole. *(Rejected: inheriting per-member job retention, which leaves
  half-purged batches and wrong aggregate counts.)*
- **Partial retry (§7.3):** cascade-cancel stays terminal. "Retry job" re-runs only the failed
  member; a separate **"Retry from here"** action (B7) re-materializes the failed member and its
  cancelled descendants as a fresh sub-DAG rather than resurrecting terminal rows. *(Rejected:
  un-cancelling terminal rows, which would poison every "terminal is final" assumption in purge,
  cascade, and metrics.)*

- **Scheduler is the subject (§3):** batch membership and continuations are generated per-scheduler
  methods — `sendEmail.AddToBatch(batch, …)`, `two.ScheduleAfterAsync(parent, …)` — so the job being
  scheduled is always the receiver, matching `EnqueueAsync`. This requires `EnqueueAsync`/`ScheduleAsync`/
  `ScheduleAtAsync` to return a `JobHandle` (carrying `.Id`) rather than a bare `string` (**B0**).
  *(Rejected: `batch.Add(scheduler, payload)` / `parent.ContinueWith(childScheduler, payload)`, which
  made the batch/handle the subject and buried the scheduled job as an argument.)*

- **Programmatic read API (§8):** `IJobBatchMonitor` (status, members, graph) and `IJobMonitor`
  (single job) return plain records for in-process consumers to serialize as they wish; `GetStatusAsync`
  is O(1) via per-state batch-row counters. *(Rejected: a batch-listing/query method — deemed
  unnecessary; and shipping a `JsonSerializerContext`/wire contract — serialization is the consumer's
  choice, kept separate from the dashboard's own JSON API.)*

- **Mid-job scheduling (§3.6):** a running member expands the workflow relative to itself via its own
  `JobDetails`. Two axes: the **verb** picks ordering + durability — `ScheduleAfter(JobDetails, …)` is
  gated after the job and buffered until it succeeds (retry-safe); `AddToBatchAsync(JobDetails, …)` is a
  concurrent, immediately-written batch member (fires even if the job later fails/retries — must be
  idempotent). `ContinuationOptions` (`Detached`/`BesideContinuations`/`BeforeContinuations`) picks
  how existing waiters relate to the new job, with `BeforeContinuations` performing an **additive**
  `{C, J}` splice (never a re-parent). `AddToBatchAsync(JobDetails, Detached)` is contradictory and
  rejected (`IJOB020`, compile-time + runtime); and when the current job has no batch, the
  batch-tracking paths (`AddToBatchAsync(JobDetails, …)` and non-`Detached` `ScheduleAfter(JobDetails, …)`)
  throw at run time, leaving `ScheduleAfter(JobDetails, …, Detached)` as the only valid batch-less
  call. *(Rejected: re-parenting waiters onto `J` alone, which drops their dependency on `C`; and a
  single ambient "current batch" — the token is the explicit `JobDetails`.)*

No open questions remain.

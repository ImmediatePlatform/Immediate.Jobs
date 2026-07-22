# Storage capabilities (segmented providers)

> **Status:** Design / implementation plan. Not yet implemented.
> **Goal:** Split the single `IJobStorage` seam into **capability interfaces** so a provider can
> implement a subset — e.g. a Redis connector that does the queue only. Batches and continuations
> require a **graph-capable** provider (a SQL database); when the active provider lacks that
> capability, the batch/continuation APIs fail fast with a clear message ("use a SQL provider").
>
> **Scope decision:** this plan is about **one active provider that advertises what it supports**.
> Running two providers at once and routing between them (a *composite* provider) is a separate,
> larger feature that this segregation *enables* but does not include — see §9.

## 1. Why

`IJobStorage` is a 32-method fat interface today. Every provider (EF Core, in-memory, single-server)
must implement all of it, including the hard parts: atomic multi-item batch/continuation graph writes.
That is exactly what non-relational stores (Redis, DynamoDB, Cassandra) are bad at and relational
stores are good at — see [`provider-suitability.md`](provider-suitability.md) for the backend matrix
and the reasoning.

We want a high-throughput queue-only Redis provider to be a **small, correct** deliverable — it should
implement the claim/lease/state-machine surface it is genuinely good at, and *not* be forced to fake
atomic batches. The mechanism is **interface segregation**: cohesive capability interfaces, a provider
implements the ones it can honor, and the runtime detects what is available and guards the rest.

## 2. Capability taxonomy

Four capabilities. The first is mandatory (a provider that can't do this isn't a job store); the rest
are optional and independently implementable.

| Capability | Interface | What it covers | Redis? | SQL? |
| --- | --- | --- | --- | --- |
| **Queue** (required) | `IJobQueueStorage` | init, enqueue, acquire/claim, lease, complete, fail, retry, delete, job history purge, heartbeat, health, job reads | ✅ | ✅ |
| **Recurring** | `IRecurringJobStorage` | upsert/pause/resume/remove schedules, due-schedule scan, occurrence materialization (dedupe) | ✅ (conditional writes) | ✅ |
| **Graph** | `IJobGraphStorage` | atomic batch + continuation writes, edge/counter maintenance, gated-completion flush, batch reads/graph, cancel/delete batch, batch history purge | ❌ | ✅ |
| **Replica** *(exists)* | `IJobStorageReplica` | mirror the exact job set an authoritative in-memory queue selected (single-server mode) | — | ✅ |

### 2.1 Method assignment (from today's `IJobStorage`)

**`IJobQueueStorage`** (the required core):
`InitializeAsync`, `EnqueueAsync`, `AcquireDueJobsAsync`, `RenewLeaseAsync`, `CompleteAsync`,
`FailAsync`, `RetryAsync`, `DeleteAsync`, `HeartbeatAsync`, `IsHealthyAsync`, `QueryJobsAsync`,
`GetJobStatusAsync`, `GetMonitoringSnapshotAsync`, and a job-history slice of `PurgeAsync` (see §3.3).

**`IRecurringJobStorage`:**
`UpsertRecurringAsync`, `RemoveRecurringAsync`, `RemoveObsoleteCodeDefinedRecurringAsync`,
`PauseRecurringAsync`, `ResumeRecurringAsync`, `GetDueRecurringAsync`, `MaterializeRecurringAsync`.

**`IJobGraphStorage`:**
`EnqueueContinuationAsync`, `EnqueueBatchAsync`, `CompleteWithContinuationsAsync`, `AddBatchJobAsync`,
`CancelBatchAsync`, `DeleteBatchAsync`, `GetBatchStatusAsync`, `QueryBatchesAsync`,
`QueryBatchMembersAsync`, `GetBatchGraphAsync`, and the batch-history slice of `PurgeAsync`.

Notes on the two cross-cutting methods:
- **`GetJobStatusAsync`** returns a job plus its incoming dependency edges. A queue-only provider
  returns the job with an **empty** dependency set (it never has edges) — no graph capability needed.
- **`GetMonitoringSnapshotAsync`** aggregates job-state counts + servers (queue capability) and
  recurring schedules (recurring capability). A queue-only, recurring-less provider simply reports no
  recurring schedules.

## 3. Interface design

### 3.1 Segregation without churn (back-compat)

The key move keeps every existing consumer and provider working unchanged: **`IJobStorage` becomes the
union of the capability interfaces**, rather than being deleted.

```csharp
public interface IJobStorage
    : IJobQueueStorage, IRecurringJobStorage, IJobGraphStorage
{
    // now empty — all members moved to the capability interfaces it inherits
}
```

- **Runtime consumers** (`JobSchedulerService`, generated schedulers) keep calling `IJobStorage` — no
  signature changes, nothing to touch.
- **Existing full providers** (`EntityFrameworkCoreJobStorage`, `InMemoryJobStorage`,
  `SingleServerJobStorage`) already implement every member, so they satisfy all three sub-interfaces
  automatically — zero code change.
- **New partial providers** (Redis) implement only `IJobQueueStorage` (+ optionally
  `IRecurringJobStorage`) and are registered through the queue-capability path (§5).

This is a pure interface-segregation refactor: no behavior changes, no migrations, independently
shippable and mergeable before any Redis work starts.

### 3.2 Capability detection

The runtime discovers what the registered provider can do by **interface checks** — idiomatic, no
parallel metadata to keep in sync:

```csharp
bool supportsGraph     = storage is IJobGraphStorage;
bool supportsRecurring = storage is IRecurringJobStorage;
```

An optional `StorageCapabilities` flags enum (surfaced on the monitoring snapshot / health endpoint)
lets the **dashboard** hide batch views and lets tooling report what's on — derived from the same
interface checks, not hand-maintained.

### 3.3 `PurgeAsync` split

`PurgeAsync` today takes job **and** batch retentions in one call. Split it along the capability line:
- `IJobQueueStorage.PurgeJobsAsync(succeededRetention, failedRetention)`
- `IJobGraphStorage.PurgeBatchesAsync(batchSucceededRetention, batchFailedRetention)`

The maintenance loop calls whichever capabilities are present. (For back-compat, the union
`IJobStorage` can retain a default `PurgeAsync` that fans out to both slices.)

## 4. Guarding "batches need SQL"

When the active provider lacks `IJobGraphStorage`, batch/continuation usage must fail **clearly and
early**, never silently. Three layers, outermost first:

1. **Startup validation.** During `AddImmediateJobs()`, if the registered storage is not
   `IJobGraphStorage`, log an informational line ("Batch & continuation features are disabled: the
   configured storage 'RedisJobStorage' implements the queue capability only. Configure a SQL provider
   to enable them.") and **do not register** `IJobBatchScheduler`.
2. **Resolve-time guard.** `IJobBatchScheduler` (and the generated `AddToBatchAsync` / `ScheduleAfterAsync`
   entry points) resolve a graph capability; when absent they throw `NotSupportedException` with the
   same actionable message. This catches code paths the startup scan can't prove are unused.
3. **No partial writes.** Because the guard trips *before* any storage write, a batch attempt on a
   queue-only provider does nothing — consistent with the atomic-batch contract.

The generated `AddToBatchAsync` / `ScheduleAfterAsync` methods still **compile** (they're emitted per job
regardless of provider); they just throw at runtime under a queue-only provider. This keeps the
generator provider-agnostic. *(Optional later: an analyzer hint if the project references only a
queue-only provider package — deferred; provider choice isn't reliably known at compile time.)*

## 5. Registration

A queue-only provider registers through a queue-capability entry point; a full provider registers as
today and lights up everything:

```csharp
// Queue-only: Redis. Batches/continuations disabled (guarded per §4).
services.AddImmediateJobs(o =>
{
    o.UseRedis("localhost:6379");   // registers an IJobQueueStorage (+ IRecurringJobStorage)
    o.MaxParallelJobs = 32;
});

// Full: SQL. Everything, exactly as today.
services.AddImmediateJobs(o =>
{
    o.UseEntityFrameworkCore<AppDbContext>();
});
```

`UseRedis` registers the Redis provider as the single `IJobStorage`-shaped service (it satisfies the
required queue capability; the graph capability is simply absent). No other configuration differs.

## 6. Redis provider scope (first partial provider)

Ships as `Immediate.Jobs.Redis` implementing **`IJobQueueStorage`** and (recommended)
**`IRecurringJobStorage`**:

- Sorted set per queue keyed by `DueAt` for due-work selection; hash per job; per-worker lease set /
  GSI-style index on `LeaseExpiresAt` for orphan reclaim.
- Claim = a Lua script (atomic select-and-mark under capacity), mirroring the optimistic-claim model.
- Recurring materialization dedupe = conditional write on `jobName#occurrence`.
- Job-history purge via key TTLs.
- **Not implemented:** `IJobGraphStorage`. Documented as "batching requires a SQL provider."
- Fair queues (per [`docs/fair-queues.md`](fair-queues.md)) slot into this provider's claim script
  later, independently — orthogonal to segregation.

## 7. Testing

- **Segregation refactor:** existing suite must pass unchanged (proves back-compat of the `IJobStorage`
  union).
- **Capability guard:** register a queue-only fake provider; assert `IJobBatchScheduler` is not
  registered, and that `AddToBatchAsync` / `ScheduleAfterAsync` throw `NotSupportedException` with the guidance
  message; assert queue/recurring paths work.
- **Dashboard:** batch views hidden when graph capability absent.
- **Redis provider:** its own queue + recurring integration tests (claim under contention, lease
  recovery, occurrence dedupe).

## 8. Phasing

**Phase 1 — Interface segregation (do first, standalone).**
Extract `IJobQueueStorage` / `IRecurringJobStorage` / `IJobGraphStorage`; make `IJobStorage` inherit
them; split `PurgeAsync`. No behavior change; full suite green. This alone unblocks partial providers.

**Phase 2 — Capability guard + detection.**
Interface-check detection, `StorageCapabilities` surfacing, startup + resolve-time guards, conditional
`IJobBatchScheduler` registration, dashboard hiding.

**Phase 3 — Redis queue-only provider.**
`Immediate.Jobs.Redis` implementing queue (+ recurring), with docs stating batching needs SQL.

**Effort:** Phase 1 is small and low-risk (mechanical, compiler-verified, no runtime change). Phase 2
is small. Phase 3 is the real build (Lua claim script, indexes, recurring dedupe) but is now a
*bounded* surface — exactly the queue capability, nothing more.

## 9. Deferred: composite provider (not now)

A **composite** runs two providers at once — e.g. standalone jobs on Redis, batch/continuation jobs on
SQL, in the same app. It is **out of scope here** and **not required** for the queue-only Redis goal
(that goal is served by single-provider capability detection above).

The segregation in this plan is precisely the foundation a composite would need, so adding it later
requires **no rework** — a composite is an `IJobStorage` that delegates each capability slice to an
inner provider. But it is **not trivial**; the genuinely non-trivial parts are all runtime:

- **Job-home routing** — `Complete`/`Fail`/`RenewLease`/`Retry` must reach the store a job lives in
  (encode the home in the client-generated id/handle, or keep a routing index).
- **Acquisition merge under global capacity** — `AcquireDueJobsAsync` must fan out to both stores and
  respect the global `MaxParallelJobs` / per-queue caps across them (sequential budget subtraction,
  ideally with a reserved minimum for the graph lane so the firehose can't starve batches).
- **Monitoring/query union** — counts, `QueryJobsAsync` pagination, and health merged across stores.
- **Hard partition rule** — anything the graph touches (batch members, continuation parents) must live
  wholly in the graph store; cross-store continuations are rejected or forced into the graph store.

Build it only when there's a concrete need to keep a high-volume standalone-job firehose off the
relational store *while still* using batches. Until then, single-provider capability detection is the
shipping feature.

## 10. Open questions

1. **Recurring on Redis** — ship recurring in the first Redis release (it's a clean conditional-write
   fit), or queue-only to start and add recurring next? Leaning: include it.
2. **`StorageCapabilities` surface** — expose on the monitoring snapshot only, or also as a typed
   property on the provider for tooling?
3. **`PurgeAsync` shape** — keep the unified `PurgeAsync` on the `IJobStorage` union as a
   back-compat default, or move callers fully onto the split `PurgeJobsAsync` / `PurgeBatchesAsync`?

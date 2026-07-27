# Storage capabilities (segmented providers)

> **Status:** Implemented.
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

Three capabilities. The first is mandatory — a store that can't do it isn't a job store — and it *is*
the base type everyone injects. The other two are optional extensions of it.

| Capability | Interface | What it covers | Redis? | SQL? |
| --- | --- | --- | --- | --- |
| **Queue** (required, the base) | `IJobStorage` | init, enqueue, acquire/claim, lease, complete, fail, retry, delete, job history purge, heartbeat, health, job reads | ✅ | ✅ |
| **Recurring** | `IRecurringJobStorage : IJobStorage` | upsert/pause/resume/remove schedules, due-schedule scan, occurrence materialization (dedupe) | ✅ (conditional writes) | ✅ |
| **Graph** | `IJobGraphStorage : IJobStorage` | atomic batch + continuation writes, edge/counter maintenance, gated-completion flush, batch reads/graph, cancel/delete batch, batch history purge | ❌ | ✅ |
| **Replica** *(exists)* | `IJobStorageReplica` | mirror the exact job set an authoritative in-memory queue selected (single-server mode) | — | ✅ |

The optional capabilities **inherit the queue base**, which is also semantically correct: recurring
schedules materialize into ordinary queue jobs, and batch members are ordinary queue jobs with edges.
Neither is meaningful without the queue underneath it, so `IRecurringJobStorage` and `IJobGraphStorage`
each `: IJobStorage`.

### 2.1 Method assignment (from today's `IJobStorage`)

**`IJobStorage`** (the required base = the queue capability):
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

Notes on the two cross-cutting methods, both of which stay on the **base**:
- **`GetJobStatusAsync`** returns a job plus its incoming dependency edges. A queue-only provider
  returns the job with an **empty** dependency set (it never has edges) — no graph capability needed.
- **`GetMonitoringSnapshotAsync`** aggregates job-state counts + servers (queue) and, when the provider
  is also `IRecurringJobStorage`, recurring schedules. A queue-only provider simply reports none.

## 3. Interface design

### 3.1 Inverted hierarchy: base + extensions (not a union)

The direction of the hierarchy is the whole ballgame. The **required queue capability is the base
type `IJobStorage`**, and each optional capability *extends* it:

```csharp
public interface IJobStorage : IAsyncDisposable
{
    // the required queue capability — init, enqueue, acquire/claim, lease,
    // complete, fail, retry, delete, reads, health, PurgeJobsAsync, async disposal
}

public interface IRecurringJobStorage : IJobStorage
{
    // + upsert/pause/resume/remove, due-scan, materialize
}

public interface IJobGraphStorage : IJobStorage
{
    // + atomic batch/continuation writes, edge/counter maintenance,
    //   batch reads, cancel/delete, PurgeBatchesAsync
}
```

**Why not the other direction.** The tempting shape is `IJobStorage : IJobQueueStorage,
IRecurringJobStorage, IJobGraphStorage` — one union interface at the *bottom* of the hierarchy. That
breaks capability detection outright: anything that satisfies `IJobStorage` necessarily satisfies all
three sub-interfaces, so

```csharp
IJobStorage storage = /* a Redis, queue-only provider */;
bool supportsGraph = storage is IJobGraphStorage;   // ALWAYS true — useless
```

is a compile-time-guaranteed `true`. There is no runtime type that is "`IJobStorage` but not
`IJobGraphStorage`," so the check can never be false. Inverting it — base at the top, capabilities
extending down — makes a queue-only provider a genuine type that *isn't* `IJobGraphStorage`, so the
check means something.

- A **full provider** (`EntityFrameworkCoreJobStorage`, `InMemoryJobStorage`, `SingleServerJobStorage`)
  declares `: IRecurringJobStorage, IJobGraphStorage` (each transitively `IJobStorage`). The method
  bodies already exist — this is a one-line change to each class declaration. A convenience marker
  `IFullJobStorage : IRecurringJobStorage, IJobGraphStorage` can shorten it, but isn't required.
- A **queue-only provider** (Redis) declares `: IJobStorage` and stops there.
- **Consumers inject the base `IJobStorage`** and cast up to a capability when they need one (§3.2, §4).

### 3.2 Capability detection (and it's nearly free)

The runtime discovers what the registered provider can do by **interface checks**:

```csharp
bool supportsGraph     = storage is IJobGraphStorage;
bool supportsRecurring = storage is IRecurringJobStorage;
```

Under the inverted hierarchy these can actually be `false`, so they carry real information. The check
is also effectively free at steady state: the concrete storage type is a process-wide singleton, so
every one of these `is` tests is **monomorphic** — one type, forever. Tier-1 PGO collapses a
monomorphic type test to a cached-class-pointer compare (and can guard/devirtualize the branch), so
gating a hot path on `storage is IJobGraphStorage` is not a real cost.

An optional `StorageCapabilities` flags enum (surfaced on the monitoring snapshot / health endpoint)
lets the **dashboard** hide batch views and lets tooling report what's on — derived from the same
interface checks, not hand-maintained.

### 3.3 `PurgeAsync` split

`PurgeAsync` today takes job **and** batch retentions in one call. Split it along the capability line:
- `IJobStorage.PurgeJobsAsync(succeededRetention, failedRetention)` — on the base.
- `IJobGraphStorage.PurgeBatchesAsync(batchSucceededRetention, batchFailedRetention)`.

The maintenance loop calls `PurgeJobsAsync` always and `PurgeBatchesAsync` only when the provider is
`IJobGraphStorage`.

### 3.4 Runtime paths that must branch on capability

Two existing runtime paths call optional-capability methods on the base seam today and must branch
once those methods move off `IJobStorage`:

- **Completion** — `JobSchedulerService` calls `CompleteWithContinuationsAsync` (a graph method) after
  every job. Under a queue-only provider there are no continuations to flush, so it must call the plain
  `CompleteAsync` instead. Resolve the branch once at startup (store an `IJobGraphStorage?` alongside
  the base) rather than per-job.
- **Recurring scan** — the `GetDueRecurringAsync` → `MaterializeRecurringAsync` loop (and code-defined
  `UpsertRecurringAsync` sync) must only run when the provider is `IRecurringJobStorage`. Skip the
  whole loop otherwise; there are no schedules to scan.

Both branches are decided once (singleton type), consistent with §3.2.

## 4. Guarding "batches need SQL"

When the active provider lacks `IJobGraphStorage`, batch/continuation usage must fail **clearly and
early**, never silently. Layers, outermost first:

1. **Startup validation.** During `AddMyAppJobs()`, if the registered storage is not
   `IJobGraphStorage`, log an informational line ("Batch & continuation features are disabled: the
   configured storage 'RedisJobStorage' implements the queue capability only. Configure a SQL provider
   to enable them.") and **do not register** `IJobBatchScheduler`.
2. **Resolve-time guard (the cast pattern).** The batch scheduler and the generated
   `AddToBatch` / `ScheduleAfterAsync` entry points inject the base `IJobStorage` and cast up, throwing
   when the capability is absent:

   ```csharp
   public async ValueTask<BatchHandle> CommitAsync(/* … */)
   {
       if (storage is not IJobGraphStorage graph)
           throw new NotSupportedException(
               "Batches & continuations require a graph-capable storage provider (a SQL database). " +
               "The configured provider implements the queue capability only.");
       await graph.EnqueueBatchAsync(/* … */);
   }
   ```

   This catches code paths the startup scan can't prove are unused, and — per §3.2 — the cast is
   monomorphic and near-free. (Alternatively the scheduler could inject `IJobGraphStorage` directly and
   rely on step 1 not registering it; the explicit guard gives a better message and one obvious place
   for it.)
3. **No partial writes.** Because the guard trips *before* any storage write, a batch attempt on a
   queue-only provider does nothing — consistent with the atomic-batch contract.

The generated `AddToBatch` / `ScheduleAfterAsync` methods still **compile** (they're emitted per job
regardless of provider); they just throw at runtime under a queue-only provider. This keeps the
generator provider-agnostic. *(Optional later: an analyzer hint if the project references only a
queue-only provider package — deferred; provider choice isn't reliably known at compile time.)*

## 5. Registration

Register the provider instance under **every capability interface it implements**, so consumers can
inject either the base or a capability and resolve the same singleton:

```csharp
// inside UseEntityFrameworkCore<T>() — a full provider
services.AddSingleton<EntityFrameworkCoreJobStorage>();
services.AddSingleton<IJobStorage>(sp => sp.GetRequiredService<EntityFrameworkCoreJobStorage>());
services.AddSingleton<IRecurringJobStorage>(sp => sp.GetRequiredService<EntityFrameworkCoreJobStorage>());
services.AddSingleton<IJobGraphStorage>(sp => sp.GetRequiredService<EntityFrameworkCoreJobStorage>());

// inside UseRedis() — a queue-only provider: base only
services.AddSingleton<IJobStorage, RedisJobStorage>();
// no IRecurringJobStorage / IJobGraphStorage registered → capability checks are false,
// IJobBatchScheduler is not registered (§4.1)
```

From the user's side nothing new is required:

```csharp
// Queue-only: Redis. Batches/continuations disabled (guarded per §4).
services.AddMyAppJobs(o =>
{
    o.UseRedis("localhost:6379");
    o.MaxParallelJobs = 32;
});

// Full: SQL. Everything, exactly as today.
services.AddMyAppJobs(o =>
{
    o.UseEntityFrameworkCore<AppDbContext>();
});
```

## 6. Redis provider scope (first partial provider)

Ships as `Immediate.Jobs.Redis` implementing **`IJobStorage`** (queue) and
**`IRecurringJobStorage`**:

- Sorted set per queue keyed by `DueAt` for due-work selection; hash per job; a lease-expiry sorted
  index for orphan reclaim. Fixed-width due/created/id members preserve deterministic ordering when
  Redis scores share a millisecond.
- Claim = a Lua script (atomic ordered select-and-mark under global, queue, and job capacity),
  including atomic lease recovery.
- Recurring materialization dedupe = one Lua conditional write over the schedule CAS, occurrence key,
  invocation, and indexes.
- Terminal completion-time indexes drive the existing configurable job-history purge API. This is
  used instead of completion-time TTLs because retention is supplied to `PurgeJobsAsync`, not to the
  terminal transition.
- `UseRedis` selects distributed mode automatically. Single-server mode requires a full-capability
  durable replica, which Redis intentionally is not.
- **Not implemented:** `IJobGraphStorage`. Documented as "batching requires a SQL provider."
- Fair queues (per [`fair-queues.md`](fair-queues.md)) slot into this provider's claim script
  later, independently — orthogonal to segregation.

## 7. Testing

- **Segregation refactor:** existing suite must pass unchanged for the full providers (proves the
  inverted hierarchy is behavior-preserving for anything that implements every capability).
- **Capability guard:** register a queue-only fake provider (implements `IJobStorage` only); assert
  `IJobBatchScheduler` is not registered, and that `AddToBatch` / `ScheduleAfterAsync` throw
  `NotSupportedException` with the guidance message; assert queue/recurring paths work; assert the
  completion path uses `CompleteAsync` and the recurring loop is skipped (§3.4).
- **Dashboard:** batch views hidden when graph capability absent.
- **Redis provider:** its own queue + recurring integration tests (claim under contention, lease
  recovery, occurrence dedupe).

## 8. Phasing

**Phase 1 — Interface segregation (do first, standalone).**
Move the recurring and graph methods off `IJobStorage` onto `IRecurringJobStorage : IJobStorage` and
`IJobGraphStorage : IJobStorage`; update the full providers' class declarations to list them; split
`PurgeAsync`; switch the handful of graph/recurring consumers to the guard-cast and add the two runtime
branches (§3.4). No behavior change for full providers; full suite green. This alone unblocks partial
providers.

**Phase 2 — Capability guard + detection.**
Interface-check detection, `StorageCapabilities` surfacing, startup + resolve-time guards, conditional
`IJobBatchScheduler` registration, dashboard hiding.

**Phase 3 — Redis queue-only provider.**
`Immediate.Jobs.Redis` implementing queue (+ recurring), with docs stating batching needs SQL.

**Effort & back-compat.** Phase 1 is a bit more than a pure marker refactor: because methods *move off*
the base, the small number of graph/recurring consumers (`JobBatchScheduler`, `JobMonitor`, the batch
dashboard endpoints, and the two `JobSchedulerService` paths in §3.4) switch from calling the method
directly to a guard-cast or a capability branch. It is still compiler-verified, migration-free, and a
no-op for anyone on a full provider. Phase 2 is small. Phase 3 is the real build (Lua claim script,
indexes, recurring dedupe) but is now a *bounded* surface — exactly the queue capability, nothing more.

## 9. Deferred: composite provider (not now)

A **composite** runs two providers at once — e.g. standalone jobs on Redis, batch/continuation jobs on
SQL, in the same app. It is **out of scope here** and **not required** for the queue-only Redis goal
(that goal is served by single-provider capability detection above).

The segregation in this plan is precisely the foundation a composite would need, so adding it later
requires **no rework** — a composite is an `IJobStorage` (+ the capabilities it can honor across its
inner providers) that delegates each capability slice to an inner provider. But it is **not trivial**;
the genuinely non-trivial parts are all runtime:

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

## 10. Resolved implementation decisions

1. **Recurring on Redis** — included in the first provider release.
2. **`StorageCapabilities` surface** — derived from interface checks, exposed on monitoring
   snapshots and health-check data, and used by the dashboard to hide unavailable views.
3. **Purge shape** — callers use the capability-aligned `PurgeJobsAsync` and `PurgeBatchesAsync`
   methods directly; there is no unified compatibility method on the queue base.

# Fair queues (SQS-style noisy-neighbor fairness)

> **Status:** Design / implementation plan. Not yet implemented.
> **Goal:** Let callers tag each enqueued job with a runtime *group id* (SQS `MessageGroupId`) so that a large backlog in one group cannot starve other groups' jobs of consumer capacity.

## 1. Semantics we are building (and not building)

Decisions taken during design:

| Question | Decision |
| --- | --- |
| Core behavior | **Fairness only.** Mirror SQS *fair queues* (the 2024 standard-queue feature), **not** SQS FIFO. |
| Ordering within a group | **None guaranteed.** No FIFO cursor. |
| In-group concurrency | **Unbounded.** If the node has capacity, many jobs of the same group run in parallel. No per-group serialization or exclusivity. |
| Group id source | **Runtime enqueue argument**, opaque string, scoped within the job's queue. |
| Ungrouped jobs | **Unrestricted, as today.** A null/empty group id means "own singleton tenant" — behaves exactly like current jobs. |
| Providers in v1 | In-memory, EF Core, single-server. (Single-server selection is delegated to the in-memory primary, so it comes for free — see §6.) |
| Activation | **Opt-in via `o.UseFairQueues(...)`.** Off by default; when not registered the acquisition path is byte-for-byte today's code with zero added cost (see §5.0 / §8). |

The **only** guarantee: a group that is consuming a disproportionate share of consumer capacity gets deprioritized so quieter groups keep flowing. Jobs are never dropped or throttled — a noisy group's jobs simply wait longer while quiet work exists, and return to normal once its backlog clears.

## 2. How AWS implements it (reference)

Source: [SQS fair queues detailed](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-fair-queues-detailed.html).

- **Tenant = `MessageGroupId`.** A message with no group id is its own distinct tenant.
- **Noisy-neighbor detection**, via two independent measures:
  - **Concurrency share** — a tenant's *in-flight* messages as a fraction of all in-flight messages in the queue. Marked noisy when the tenant has **> 10 %** of in-flight messages **and** at least **30** of its own in flight.
  - **Processing-time share** — the tenant's recent share of total consumer processing time. Marked noisy when **> 10 %**. (Catches few-but-slow tenants that never build up in-flight count.)
- **On receive:** once a tenant is noisy, consumers are served messages from quiet tenants whenever quiet messages exist. Among multiple noisy tenants, the one with the **fewest in-flight** messages is served first.
- **Reset:** a tenant stops being noisy when its backlog is fully consumed **or** it has had no in-flight messages for **5 continuous minutes**.
- **Caveat AWS calls out:** the concurrency-share measure only works if consumers run enough messages concurrently for one tenant to stand out. Size the fleet accordingly.

## 3. The one decision that matters: in-flight fairness vs. backlog fairness

This deserves a call-out because AWS's real mechanism does **not**, on its own, deliver the example that motivated this work:

> "There's a backlog of 1000 jobs for group 1, and one job for group 2 comes in — that one is next in line."

AWS detects noisy neighbors from **in-flight** count, not **backlog**. If your worker concurrency is, say, 10, group 1 can never have ≥ 30 jobs in flight, so it is **never marked noisy**, and group 2's single (newer) job simply queues behind the 1000 older group-1 jobs by `DueAt`. AWS resolves the scenario only *over time*, as group 1 accumulates enough in-flight/processing share to trip the threshold — and it leans on large consumer fleets to make that happen quickly.

We have two options:

- **Option A — Faithful AWS (in-flight fairness).** Deprioritize a group only once it is detected noisy by concurrency share (and later, processing-time share). Simple, matches AWS exactly, but on low-concurrency workers your stated example is only satisfied eventually, not immediately.
- **Option B — Backlog-aware round-robin (recommended).** In addition to noisy-neighbor deprioritization, order the *eligible* candidates by round-robin across distinct groups rather than pure `DueAt`. This directly delivers "1 lone group-2 job jumps ahead of 1000 backlogged group-1 jobs," independent of worker concurrency. It is a superset of Option A's mechanism using the same ordering key (§5).

**Decision: Option B.** We implement backlog-aware round-robin across groups, with the noisy-neighbor concurrency-share signal layered on top, and expose a config switch (`GroupRoundRobin`) to fall back to faithful AWS (Option A) if ever desired. Option B is what actually matches the intended behavior ("1 lone group-2 job jumps ahead of a 1000-job group-1 backlog"); the noisy-neighbor signal remains useful for the "few-but-hogging-capacity" case.

The rest of this plan is written against **Option B**, and notes where Option A is a strict subset.

## 4. Data model & API changes

### 4.1 `JobRecord`

Add one optional field to `src/Immediate.Jobs.Shared/JobModels.cs`:

```csharp
/// <summary>Optional fairness group (tenant) key, scoped within the queue. Null = own singleton tenant.</summary>
public string? GroupId { get; init; }
```

### 4.2 EF Core entity + schema

- Add `GroupId` (nullable `varchar(128)`) to `ImmediateJobEntity`.
- Add a covering index for the fairness query and candidate selection:
  `(QueueName, State, GroupId)` — used to count in-flight per group and to order candidates.
  Confirm it composes with the existing acquisition index (`QueueName, JobName, State, DueAt`).
- Migration: additive, nullable column — no backfill needed. Update `ImmediateJobsModelBuilderExtensions`.

### 4.3 In-memory store

`InMemoryJobStorage` keeps `JobRecord` directly, so the new field is carried automatically. No schema step.

### 4.4 EnqueueAsync API (source generator)

The enqueue entry points live in the **non-generated** base `JobScheduler<TPayload>` (`src/Immediate.Jobs.Shared/JobSchedulers.cs`) — `EnqueueAsync`, `ScheduleAsync`, `ScheduleAtAsync`, and `CreateRecord`. This is the clean seam:

- Add an optional `string? groupId = null` parameter to `EnqueueAsync`, `ScheduleAsync`, `ScheduleAtAsync` (and the `IJobScheduler<TPayload>` interface).
- Thread it into `CreateRecord` → `JobRecord.GroupId`.
- **Validation (decided):** normalize empty/whitespace to `null` (ungrouped = own singleton tenant); reject group ids longer than **128** characters with `ArgumentException`. Matches SQS's `MessageGroupId` limit and keeps the index key tight.

```csharp
public ValueTask<JobHandle> EnqueueAsync(
    TPayload payload,
    string? groupId = null,
    CancellationToken cancellationToken = default) =>
    ScheduleAtAsync(payload, TimeProvider.GetUtcNow(), groupId, cancellationToken);
```

Because these are inherited (not emitted per-job), the Scriban template `Templates/Job.sbntxt` needs **no** change for the basic case. Continuation/batch/recurring enqueue paths can gain the parameter in a later pass if grouping is wanted there; v1 targets the direct `EnqueueAsync`/`ScheduleAsync` paths.

Usage:

```csharp
await welcomeEmail.EnqueueAsync(new(userId, "v2"), groupId: tenantId, cancellationToken);
```

## 5. The fairness algorithm (acquisition)

### 5.0 Gating — the fairness path is opt-in

Fairness is behind a single branch at the top of `AcquireDueJobsAsync`:

- **Not registered (default):** run the **exact current selection** — `OrderBy(DueAt).ThenBy(CreatedAt).ThenBy(Id)` + `Take` — with **no** grouped-count query and **no** window function. Zero cost, unchanged query plan. This matters because acquisition is the hottest path in the system (every worker polls it continuously); the fairness machinery (an extra `GROUP BY` round-trip + `ROW_NUMBER` ordering) must not tax users who don't use it.
- **Registered via `o.UseFairQueues(...)`:** run the fairness ordering below.

The `GroupId` column and the `groupId` enqueue parameter are **always** present regardless of registration — a nullable column is free when unused, and an optional arg is harmless. Only the *dispatch ordering behavior* is gated. A group id supplied while fair queues are not registered is persisted but **inert** (no fairness effect); we log a one-time startup/debug warning rather than throwing on a stored-but-unused value.

**Cheap fast-path even when enabled:** for any queue whose current candidate window contains no non-null `GroupId`, skip the grouped-count query and window function and fall back to the simple order. So enabling fairness globally does not tax queues that happen not to use grouping.

### 5.1 Ordering

The behavioral change lives in `AcquireDueJobsAsync`. Today, per queue, candidates are ordered by `DueAt → CreatedAt → Id` up to per-queue and per-job-name capacity. We insert a fairness ordering key **in front of** that existing order. Everything else (capacity accounting, lease, claim) is unchanged.

Per queue, once per acquisition pass:

1. **Count in-flight per group.**
   `inflight[g] = count(State == Active && QueueName == q && GroupId == g)`.
   Let `N = total Active in q`. Ungrouped jobs each count as their own tenant (in-flight ≤ 1 apiece).

2. **Mark noisy groups** (concurrency-share, faithful to AWS):
   `noisy(g) = inflight[g] >= MinInflightForNoisy (30) && inflight[g] / N > ConcurrencyShareThreshold (0.10)`.
   Ungrouped jobs are never noisy.

3. **Order eligible due candidates** by this composite key (ascending):
   1. `noisy(group)` — quiet groups (0) before noisy groups (1).
   2. among **noisy** only: `inflight[group]` ascending — fewest-in-flight noisy tenant first (AWS tie-break).
   3. **[Option B]** round-robin rank across distinct groups — the *k*-th job of any given group sorts after the *(k-1)*-th job of every other group, so one job per group is interleaved before a group's second job is considered.
   4. `DueAt → CreatedAt → Id` — existing order, as the final tiebreak.

4. **Take up to capacity**, honoring existing per-queue capacity and per-job-name `MaxConcurrency`, exactly as today.

Properties:

- **Stateless / self-resetting.** Noisy status is recomputed each pass from current `Active` counts, so AWS's "reset when backlog clears" is automatic; the 5-minute idle reset only matters for the stateful processing-time measure (§7, deferred).
- **Ungrouped == today.** If no job carries a group id, every group is quiet, round-robin rank is uniform, and ordering collapses to `DueAt → CreatedAt → Id` — byte-for-byte current behavior.
- **Option A is a strict subset:** drop step 3.3 to get faithful AWS.

### 5.2 Round-robin rank (Option B) — how to compute it cheaply

For the candidate set being considered in a pass, assign each job a per-group sequence number (0,1,2,… in `DueAt` order within its group); order primarily by that sequence number, then by group signals. In-memory this is a `GroupBy` + index. In SQL it is `ROW_NUMBER() OVER (PARTITION BY GroupId ORDER BY DueAt, CreatedAt, Id)` as the leading sort term. This is bounded by the candidate window (`Take(queueCapacity)` territory), not the whole table.

## 6. Per-provider implementation

There are only **two** real selection sites:

Both selection sites branch on whether fairness is registered (§5.0): unregistered ⇒ the existing code path untouched; registered ⇒ the fairness ordering. In-memory, this branch is essentially free; in EF Core it avoids the extra query and heavier plan entirely when off.

### 6.1 `InMemoryJobStorage.AcquireDueJobsAsync`
- When fairness is off, leave the current sort exactly as-is.
- When on: compute `inflight[]`/`N` from `_jobs.Values` under the existing `_gate` lock and replace the `OrderBy(DueAt).ThenBy(CreatedAt).ThenBy(Id)` candidate sort with the composite key from §5.1.
- Everything else (capacity loop, state transition) unchanged.

### 6.2 `SingleServerJobStorage` — **free**
Its `AcquireDueJobsAsync` delegates selection to `_primary` (an `InMemoryJobStorage`) and only mirrors the chosen ids to the durable replica via `IJobStorageReplica.AcquireJobsAsync`. Fixing the in-memory selection (§6.1) covers single-server automatically. No change needed beyond carrying `GroupId` through the replica's `AcquireJobsAsync`/`Copy`.

### 6.3 `EntityFrameworkCoreJobStorage.AcquireDueJobsAsync`
- When fairness is off, keep the current query and ordering unchanged — no grouped-count round-trip, no window function.
- When on (and the fast-path in §5.0 doesn't short-circuit), add a grouped in-flight count query per queue:
  `SELECT GroupId, COUNT(*) FROM jobs WHERE QueueName=@q AND State=Active GROUP BY GroupId` → build `inflight[]`, `N`, and the (small) noisy-group set in memory.
- Modify the candidate query's `ORDER BY` to prepend the fairness key. Noisy groups are few (each > 10 % share ⇒ < 10 of them), so passing them as a parameter list into a `CASE`/`ROW_NUMBER` expression is cheap.
- The existing claim loop (per-candidate optimistic concurrency via `ConcurrencyStamp`) is untouched — we only reorder which candidates are attempted first.
- Carry `GroupId` through `Copy`, `ToRecord`, and the entity mapping.

## 7. Processing-time share (deferred to Phase 2)

The second AWS measure — deprioritize a group whose jobs are few but slow — needs a rolling, cluster-wide accumulator of per-group processing time (a windowed sum keyed by group, updated on `Complete`/`Fail`, with the 5-minute idle decay). That is genuinely stateful and provider-specific.

**Recommendation:** ship Phase 1 with concurrency-share + round-robin only. It delivers the motivating example. Add processing-time share later behind the same ordering key (an extra `noisy(g)` disjunct) once we decide where to store the rolling window (candidate: a small `group_load` table / in-memory ring per node).

## 8. Configuration & activation

Fair queues are **opt-in**: calling `UseFairQueues` on the Immediate.Jobs options builder is what enables the fairness dispatch path (§5.0). The registration *is* the on-switch — there is no separate `FairnessEnabled` boolean. Not calling it leaves acquisition byte-for-byte as today.

```csharp
services.AddImmediateJobs(o =>
{
    // ...
    o.UseFairQueues(); // defaults below
    // or tune:
    o.UseFairQueues(f =>
    {
        f.ConcurrencyShareThreshold = 0.10;
        f.MinInflightForNoisy = 30;
        f.GroupRoundRobin = true;
    });
});
```

The knobs live on a `FairQueueOptions` passed to the callback (SQS defaults). **Decided: global scope for v1** — one set of values for the whole app, no per-queue override. Fairness is still computed per-queue at runtime; per-queue *threshold* overrides via `QueueDefinitionAttribute` can be added later if a real need appears (§12).

| Setting | Default | Meaning |
| --- | --- | --- |
| `ConcurrencyShareThreshold` | `0.10` | Noisy if in-flight share exceeds this. |
| `MinInflightForNoisy` | `30` | Minimum own in-flight before noisy applies. |
| `GroupRoundRobin` (Option B) | `true` | Interleave eligible candidates across groups. `false` ⇒ faithful AWS (Option A). |

## 9. Observability

- **Dashboard:** show `GroupId` on job detail; add an optional `GroupId` filter to `JobQuery` and the jobs list.
- **Monitoring snapshot:** optionally surface top in-flight groups per queue and which are currently marked noisy — useful for tuning the thresholds.
- **Telemetry:** tag existing enqueue/acquire metrics with group id (bounded-cardinality caution — make it opt-in).

## 10. Testing

- **Unit (in-memory, deterministic):** seed N `Active` group-1 jobs to force noisy, plus pending group-2 job; assert group-2 is acquired before remaining group-1 jobs. Toggle `GroupRoundRobin` to assert Option A vs B ordering.
- **Ungrouped regression:** with no group ids, assert acquisition order is identical to pre-change (`DueAt → CreatedAt → Id`).
- **EF Core:** integration test with a real/containerized DB verifying the grouped count query, ordering, and that concurrent claim still resolves via `ConcurrencyStamp`.
- **Single-server:** verify primary selection drives the replica mirror unchanged.
- **Capacity interaction:** confirm fairness ordering never violates per-queue capacity or per-job `MaxConcurrency`.

## 11. Scope & phasing

**Phase 1 (this plan):**
1. `JobRecord.GroupId` + EF column/index/migration (always present, independent of activation).
2. `groupId` param on `EnqueueAsync`/`ScheduleAsync`/`ScheduleAtAsync` in `JobScheduler<TPayload>`, with 128-char validation.
3. `o.UseFairQueues(...)` registration + `FairQueueOptions`; the acquisition gating branch (§5.0) incl. the no-grouped-work fast-path and the inert-group-id startup warning.
4. Fairness ordering (concurrency-share + optional round-robin) in `InMemoryJobStorage` and `EntityFrameworkCoreJobStorage`; single-server inherited.
5. Tests + docs.

**Phase 2 (later):** processing-time-share measure; grouping on continuation/batch/recurring enqueue paths; dashboard/monitoring surfacing.

**Effort:** Phase 1 is moderate. The mechanical parts (field, API param, config, in-memory ordering, single-server) are small and low-risk. The bulk of the effort and the only real risk is the EF Core acquisition query — getting the grouped-count + fairness `ORDER BY` correct and efficient, and keeping the optimistic-concurrency claim loop intact under load.

## 12. Resolved decisions

All design questions are settled for Phase 1:

1. **Option A vs B** — **Option B** (backlog-aware round-robin), with `GroupRoundRobin` config to fall back to Option A. See §3.
2. **Group id validation** — normalize empty/whitespace to `null`; cap at **128** chars (SQS limit), reject longer with `ArgumentException`. Column `varchar(128)`. See §4.
3. **Threshold scope** — **global only** for v1 via `FairQueueOptions`; per-queue override deferred until a concrete need arises. See §8.
4. **Cardinality guard** — **none.** The singleton-tenant model degrades gracefully: a unique id per job just makes every job its own quiet tenant (≈ ungrouped/today), and the grouped-count query is bounded by in-flight (`Active`) rows, which are few. Optional dev-time cardinality warning can be revisited in Phase 2 if it proves useful.
5. **Activation & performance** — **opt-in via `o.UseFairQueues(...)`**, not always-on. Off by default ⇒ the hot acquisition path is unchanged (no grouped-count query, no window function). `GroupId` column and `groupId` enqueue arg exist regardless but are inert until registered. Even when enabled, queues with no grouped work in the candidate window short-circuit to the simple order. See §5.0 / §8.

# Fair queues (SQS-style noisy-neighbor fairness)

> **Status:** Implemented and verified across the supported target frameworks and storage-provider matrix.
> **Goal:** Let callers tag directly enqueued jobs with a runtime group id so a large backlog in one group cannot starve quieter groups of consumer capacity.

## 1. Final behavior

| Question | Decision |
| --- | --- |
| Core behavior | Fairness only. This is not FIFO: jobs within a group are neither serialized nor given a new ordering guarantee. |
| Group id | An opaque runtime string scoped to a queue. Empty or whitespace input is normalized to `null`; values longer than 128 characters are rejected. |
| Ungrouped work | A job with no group id is its own singleton tenant. When no grouped work is eligible, acquisition uses the existing order exactly. |
| Backlog fairness | Stateful round-robin service across groups, enabled by default when fair queues are enabled. This makes a quiet group's job advance even when acquisition capacity is one. |
| Noisy-neighbor fairness | A group with a disproportionate share of non-expired in-flight jobs is served after quiet groups. |
| Activation | Opt-in through `UseFairQueues(...)`. Group ids may be persisted while fairness is disabled, but they do not affect acquisition. |
| Phase 1 providers | In-memory, EF Core, LinqToDB, and single-server. Direct distributed acquisition by Redis rejects a fair-queue request in this version. |

The implementation does not drop, throttle, or serialize a noisy group's jobs. It changes only which eligible job is claimed next while other groups have work.

## 2. Why the original stateless plan changed

The initial proposal used a per-acquisition `ROW_NUMBER() OVER (PARTITION BY GroupId ...)`. That interleaves groups inside one candidate batch, but its rank starts over on every poll. With acquisition capacity one, the oldest job in one group can therefore win every poll and continue starving another group.

Phase 1 instead keeps a durable last-served cursor for each `(QueueName, GroupId)` and advances it with the job claim. The cursor survives acquisition calls and scheduler nodes, so the capacity-one case has the same fairness guarantee as a larger batch. The in-memory provider keeps the equivalent cursor under its existing lock.

The cursor deliberately has no queue-wide counter row. A single `NextSequence` row would be updated by every claim in the queue and would serialize otherwise independent scheduler nodes on the hottest path. Instead, an acquisition reads the greatest group order it currently observes and assigns the selected group `max + 1`. Concurrent nodes may assign the same value to different groups; that creates a temporary ordering tie, not a correctness failure. A later claim advances one of the tied groups and the order converges again.

A wall-clock `LastServedAt` was considered but rejected. Worker clock skew can place a group far in the past or future, while database timestamp precision differs by provider. A logical order derived from the already-read group cursors has neither dependency.

This is intentionally backlog-aware. AWS SQS noisy-neighbor detection is based on in-flight and processing-time share; in-flight thresholds alone cannot promptly solve a 1,000-to-1 backlog on a low-concurrency worker.

## 3. Public model and API

### 3.1 `JobRecord`

`JobRecord` gains:

```csharp
/// <summary>Optional fairness group key, scoped within the queue.</summary>
public string? GroupId { get; init; }
```

The value is copied through every provider representation so it survives persistence, restart, leasing, and single-server replication.

### 3.2 Source-compatible scheduler overloads

The existing cancellation-token overloads remain unchanged:

```csharp
ValueTask<JobHandle> EnqueueAsync(
    TPayload payload,
    CancellationToken cancellationToken = default);
```

Group-aware overloads are added alongside them:

```csharp
ValueTask<JobHandle> EnqueueAsync(
    TPayload payload,
    CancellationToken cancellationToken,
    string? groupId);
```

`ScheduleAsync` and `ScheduleAtAsync` follow the same pattern. The cancellation token remains in its
existing positional slot, so calls such as `EnqueueAsync(payload, default)` remain unambiguous.
Grouped calls normally name the final argument:

```csharp
await scheduler.EnqueueAsync(payload, cancellationToken, groupId: tenantId);
```

The new `IJobScheduler<TPayload>` members have default interface implementations that delegate
null, empty, and whitespace-only group ids to the existing methods and reject a normalized non-empty
group id. Existing third-party scheduler implementations therefore remain compatible, while
Immediate.Jobs' generated and testing schedulers override the grouped operations.

The non-generated `JobScheduler<TPayload>` normalizes the value once:

- `null`, empty, or whitespace-only becomes `null`;
- a value longer than 128 characters throws `ArgumentException`;
- otherwise the original opaque value is stored on `JobRecord.GroupId`.

Direct enqueue and schedule entry points are in Phase 1. Group parameters for continuations, batches, and recurring definitions remain Phase 2 work.

## 4. Configuration and request propagation

Fair acquisition is disabled unless the application opts in:

```csharp
services.AddImmediateJobs(options =>
{
    options.UseFairQueues();

    // Or:
    options.UseFairQueues(fair =>
    {
        fair.ConcurrencyShareThreshold = 0.10;
        fair.MinInflightForNoisy = 30;
        fair.GroupRoundRobin = true;
    });
});
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `ConcurrencyShareThreshold` | `0.10` | A group is potentially noisy when its share is strictly greater than this value. Must be in `(0, 1]`. |
| `MinInflightForNoisy` | `30` | Minimum non-expired in-flight jobs in the group before it can be noisy. Must be positive. |
| `GroupRoundRobin` | `true` | Use the stateful group cursor. `false` retains noisy-neighbor prioritization without backlog round-robin. |

`ImmediateJobsOptions` converts these mutable startup options to an immutable `FairQueuePolicy` carried on each `JobAcquisitionRequest`. A `null` policy means the provider must execute its existing acquisition path.

If grouped jobs are acquired while the policy is disabled, the scheduler emits a one-time warning that the stored group ids are inert.

## 5. Persistence model

### 5.1 Job column and index

All durable job representations gain nullable `GroupId` with a maximum length of 128. EF Core and LinqToDB add an index shaped for group-state lookups:

```text
(QueueName, State, GroupId)
```

The existing acquisition indexes remain in place.

### 5.2 SQL cursor table

EF Core and LinqToDB add one internal entity with the same table shape:

```text
immediate_fair_queue_groups
  QueueName             composite primary key
  GroupId               composite primary key
  LastServedSequence    bigint
  ConcurrencyStamp      concurrency token
```

`LastServedSequence` is a logical, queue-local service order. Before selecting a group, the fair path reads the maximum value visible in that queue. It assigns the chosen group the next local value. The value is monotonic within one acquisition pass but is not required to be globally unique: concurrent passes may use the same value without compromising job ownership or eventual rotation.

The selected job and its group cursor are updated in the same EF transaction. The job's existing `ConcurrencyStamp` remains the authority for exclusive ownership. The group cursor has its own concurrency token so a delayed writer cannot move a group backward. Concurrent claims from the same group, or concurrent insertion of the same new group row, retry after their conflict. Different groups do not contend even when concurrent nodes assign them the same logical sequence.

Applications own their EF migrations. Adopting this version requires a migration that adds `GroupId`, its index, and the cursor table; the library does not ship an application-specific migration. LinqToDB's schema bootstrap creates the same table for new installations, while existing databases need an equivalent additive schema upgrade.

SQL group identity follows the database collation of `GroupId`. Candidate heads, in-flight counts, cursor lookup, and cursor updates all use database-side equality so a case-insensitive database cannot split one logical group between SQL and in-memory state. Applications that need case-sensitive or trailing-space-sensitive group ids must configure an appropriate binary/ordinal collation for both `GroupId` columns in their migration.

### 5.3 In-memory cursor state

`InMemoryJobStorage` keeps group last-served values in a dictionary protected by its existing `_gate`. It derives the next logical order from the maximum value in the queue. Candidate selection, claim, and cursor advancement occur inside the same critical section.

### 5.4 LinqToDB schema

LinqToDB carries `GroupId`, adds the group-state index, and creates `immediate_fair_queue_groups` in every supported schema-bootstrap dialect. Its distributed acquisition path uses the same logical cursor and transactional claim semantics as EF Core. Existing databases need the equivalent additive schema upgrade.

### 5.5 Redis

Redis persists `GroupId` as part of the job record but does not implement the cluster-wide fair cursor in Phase 1. Direct Redis acquisition rejects a fair-queue request. This makes the provider boundary explicit instead of silently ignoring the policy.

## 6. Acquisition algorithm

Acquisition remains queue-local and continues to honor the existing global batch size, queue capacity, job-name capacity, leases, and optimistic claims.

### 6.1 Disabled and no-group fast paths

- When `JobAcquisitionRequest.FairQueues` is `null`, execute the pre-existing candidate query and order unchanged.
- When fairness is enabled but a queue has no eligible job with a non-null group id, use that same existing path for the queue.

The existing order is:

```text
DueAt, CreatedAt, Id
```

This keeps the feature opt-in and avoids cursor or group-count work for ungrouped queues.

### 6.2 Reclaim before measuring

Expired leases are returned to pending state before fairness is calculated. Only `Active` jobs whose lease has not expired contribute to in-flight counts. A stale lease therefore cannot keep a group classified as noisy.

### 6.3 Noisy-group classification

For one queue:

```text
inflight[g] = count(
  State == Active
  && LeaseExpiresAt > now
  && QueueName == queue
  && GroupId == g)

N = total non-expired Active jobs in the queue

noisy(g) =
  inflight[g] >= MinInflightForNoisy
  && inflight[g] / N > ConcurrencyShareThreshold
```

Ungrouped jobs are always quiet. Among noisy groups, fewer in-flight jobs sort first.

### 6.4 Stateful group selection

For each available slot, the provider must consider the head eligible job from every group; it must not truncate candidates by due time before group selection. Candidates are ranked by:

1. quiet before noisy;
2. among noisy groups, lower in-flight count first;
3. when `GroupRoundRobin` is enabled, the group's `LastServedSequence` ascending;
4. `DueAt`, `CreatedAt`, and `Id`.

A group without a cursor is treated as never served and therefore advances ahead of already-served backlogged groups. After a grouped job is claimed:

1. compute one more than the greatest group sequence observed for the queue;
2. store that value as the group's `LastServedSequence`;
3. re-rank remaining candidates for the next slot.

Re-ranking per slot is important: a single acquisition batch cannot consume repeatedly from one group before considering the others.

When a group's pending, scheduled, and active backlog clears, its cursor is removed. A later, genuinely new backlog then rejoins as a never-served group rather than inheriting historical scheduling debt. This intentionally lets an intermittent quiet group jump ahead when it returns.

Cursor cleanup is fairness metadata maintenance, not job correctness state. An enqueue racing with cleanup may leave the cursor absent or retained for one pass; either result self-corrects after the next successful claim. Cleanup must never be allowed to roll back or corrupt a valid job transition.

### 6.5 Ungrouped behavior

Each ungrouped job behaves as a singleton tenant:

- it is always quiet;
- it has no persistent group cursor;
- the existing `DueAt`, `CreatedAt`, `Id` order decides between ungrouped jobs;
- when all eligible work is ungrouped, the fast path produces the exact pre-feature behavior.

### 6.6 `GroupRoundRobin = false`

Disabling round-robin removes step 3 from the ranking. Quiet groups still precede noisy groups, and the existing due-time order breaks remaining ties. This is the Phase 1 approximation closest to AWS concurrency-share fairness.

## 7. Provider behavior

### 7.1 In-memory

The in-memory provider computes in-flight counts and selects candidates under `_gate`. The stateful cursor and job state change are atomic relative to other in-memory operations.

### 7.2 EF Core

The EF provider uses group cursor rows rather than a queue-wide counter. It reads the relevant non-expired active counts and the head eligible candidate for each group, selects the next group, and atomically claims the job and advances cursor state. Job concurrency conflicts are retried by re-reading current state.

The fairness branch changes selection only. Existing capacity calculation, lease ownership, retry state, and job-name concurrency limits still apply.

This stateful guarantee costs more than the disabled path. The SQL providers already write each optimistic job claim separately; fair acquisition piggybacks the group-cursor update on that claim transaction, so it does not add a separate cursor-write round-trip. It does add a selection re-read when another slot remains, because the next group must be chosen using the cursor just advanced. Thus a fair batch is an iterative select-and-claim loop rather than one `ORDER BY ... TAKE n` candidate read. The feature remains opt-in, and queues without grouped eligible work retain the original batched candidate query.

The first implementation favors the capacity-one guarantee and cross-node correctness over a provider-specific bulk SQL optimization. A later optimization may select and claim a balanced block in one database command, provided it preserves per-job capacity limits and the same cursor semantics.

### 7.3 Single-server

`SingleServerJobStorage` delegates selection to its in-memory primary. The primary applies the fair policy and the durable replica mirrors the selected ids, so EF Core or LinqToDB replicas do not run a second fairness decision.

### 7.4 LinqToDB

The LinqToDB provider implements the same stateful selection rules with provider-neutral transactions and compare-and-swap updates. Its disabled and ungrouped fast paths retain the existing batched query. Schema bootstrap creates the group cursor table for supported SQL dialects; it does not mutate existing tables.

### 7.5 Unsupported direct distributed provider

Direct Redis fair acquisition throws `NotSupportedException` in Phase 1. Redis continues to work normally when fair queues are not enabled. This is a deliberate provider boundary, not a silent downgrade.

### 7.6 Operational tradeoffs

Compared with the rejected stateless window rank, the stateful design adds one internal table, an application migration, cursor reads and writes, and cleanup behavior. It also makes enabled EF acquisition more iterative. In exchange, fairness works when capacity is one and persists across scheduler nodes.

There is no queue-wide mutable row, so unrelated groups do not contend on a shared sequence record. Claims for the same group still serialize their small claim-and-cursor transactions through that group's row. This can cause scoped retry churn for one dominant group, but does not serialize the entire queue. A sole grouped tenant still records service history so a quiet group that arrives later can advance immediately; only a queue with no eligible grouped work can take the cursorless fast path.

Group cardinality should track tenant count, not job count. A unique non-null group id per job makes fair selection scale with the number of distinct groups and creates a cursor for every job; callers should pass `null` for ungrouped work instead.

Storage lifetime is explicit: `IJobStorage` extends `IAsyncDisposable`, and built-in providers release connections or owned inner stores during asynchronous disposal. File-backed SQLite test fixtures dispose every storage instance, clear provider pools, and only then delete the database file, which keeps teardown portable to Windows.

## 8. Verification

The verified suite covers:

- **Capacity-one starvation regression:** after acquiring and completing a group-1 job, a group-2 job enqueued afterward is selected before the next group-1 backlog item, including beyond a deep backlog.
- **Batch interleaving:** one acquisition request re-ranks after each claim and distributes available slots across groups.
- **Cross-instance EF behavior:** two EF storage instances cannot duplicate a claim or corrupt the shared cursor.
- **Concurrent cursor ties:** duplicate logical sequence values converge without starving an unserved group.
- **SQL collation:** candidate state uses the same database equality as group selection on SQL Server.
- **Noisy neighbor:** a group over both thresholds is served after quiet work; expired leases do not count.
- **Round-robin switch:** `GroupRoundRobin = false` disables cursor ordering while retaining noisy classification.
- **Ungrouped regression:** acquisition order without group ids remains `DueAt`, `CreatedAt`, `Id`.
- **Capacity regression:** fairness never exceeds queue, job-name, or request capacity.
- **Cursor reset and cleanup failure:** clearing a group's backlog removes its scheduling history, while cleanup failure cannot roll back a committed terminal transition.
- **Activation:** grouped jobs are inert when the policy is disabled, with a one-time scheduler warning.
- **Validation:** whitespace normalizes to `null`; 129-character group ids fail before persistence.
- **LinqToDB parity:** direct distributed LinqToDB passes the same rotation, classification, capacity, cursor-reset, and contention cases as EF Core.
- **Provider boundary:** direct Redis rejects a fair policy; its normal acquisition remains unchanged when it is absent.
- **Single-server:** primary selection and replica mirroring preserve `GroupId` and the chosen ids.

Verification includes the full solution build, every target framework in the unit and functional suites, the EF Core/LinqToDB matrix on SQLite, PostgreSQL, and SQL Server, Redis integration tests, and dashboard lint, type, and unit checks.

## 9. Phase 1 deliverables

1. `JobRecord.GroupId` and provider persistence mappings.
2. Source-compatible direct enqueue/schedule overloads with normalization and length validation.
3. `FairQueueOptions`, `UseFairQueues(...)`, immutable acquisition policy, and disabled-policy warning.
4. In-memory queue/group cursor state and fair acquisition.
5. EF Core group-cursor entity, schema configuration, transactional acquisition, and migration guidance.
6. Single-server propagation through its in-memory primary.
7. LinqToDB cursor schema and direct distributed fair acquisition; explicit Redis rejection.
8. Focused regression, concurrency, provider-boundary, and validation tests.
9. Async storage disposal and Windows-safe file-backed fixture teardown.
10. Dashboard group display in job lists and details.
11. README and provider guidance aligned with the verified behavior.

## 10. Deferred work

- Processing-time-share detection for groups whose few jobs consume disproportionate execution time.
- The five-minute idle decay needed by that processing-time signal.
- Group parameters for continuation, batch, and recurring enqueue paths.
- `JobQuery` group filtering and per-group monitoring snapshots.
- Per-queue threshold overrides.
- Direct distributed fair cursors for Redis.
- Opt-in telemetry dimensions, subject to cardinality controls.

## 11. Resolved decisions

1. Backlog fairness uses a stateful cursor, not a stateless SQL window rank.
2. The cursor is scoped to `(QueueName, GroupId)` and advanced atomically with the claim.
3. There is no queue-wide sequence row. The next logical order is derived from the greatest observed group order; duplicate values from concurrent nodes are harmless temporary ties.
4. Empty group ids normalize to `null`; the maximum length is 128.
5. Existing cancellation-token scheduler overloads remain source compatible, including positional `default` calls; grouped parameters follow the cancellation token.
6. Fair acquisition is opt-in and has an exact disabled path plus an ungrouped fast path.
7. Thresholds are global in Phase 1.
8. Ungrouped jobs are singleton quiet tenants and do not receive durable cursor rows.
9. Cursor state is cleared with the group's backlog; returning quiet groups intentionally re-enter as never served.
10. EF applications supply their own migrations.
11. Phase 1 direct distributed SQL support includes EF Core and LinqToDB; Redis rejects fair acquisition rather than ignoring it.
12. Processing-time fairness and group observability remain Phase 2.
13. Non-null group ids identify reusable tenants. Unique-per-job group ids are an unsupported usage pattern; ungrouped work should use `null`.

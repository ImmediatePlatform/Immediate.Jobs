# Fair queues

Fair queues let callers associate directly enqueued jobs with a runtime group id so that a large
backlog in one group cannot starve quieter groups of worker capacity.

Fairness changes only which eligible job is claimed next. It does not provide FIFO ordering,
serialize jobs within a group, throttle jobs, or change existing capacity and lease rules.

## Behavior

| Area | Behavior |
| --- | --- |
| Activation | Opt in with `UseFairQueues(...)`. |
| Group identity | An opaque string scoped to a queue. |
| Backlog fairness | Groups rotate by durable last-served order, including when acquisition capacity is one. |
| Noisy-neighbor fairness | Groups consuming a disproportionate share of non-expired in-flight capacity are served after quiet groups. |
| Ungrouped jobs | Always quiet, ordered normally, and never assigned a persistent group cursor. |
| In-group ordering | No additional ordering guarantee. |
| In-group concurrency | Unrestricted apart from the existing queue and job concurrency limits. |

A group id is normalized before persistence:

- `null`, empty, or whitespace-only values become `null`;
- values longer than 128 characters throw `ArgumentException`;
- every other value is stored unchanged.

Group identity in SQL follows the database collation. Applications that require case-sensitive or
trailing-space-sensitive ids must configure an appropriate collation for both `GroupId` columns.

Group cardinality should track tenant count rather than job count. Use `null` for ungrouped work
instead of assigning a unique non-null group id to every job.

## Enqueuing grouped jobs

`JobRecord` carries the persisted group:

```csharp
public string? GroupId { get; init; }
```

Direct enqueue and scheduling operations provide group-aware overloads:

```csharp
ValueTask<JobHandle> EnqueueAsync(
    TPayload payload,
    string? groupId,
    CancellationToken cancellationToken);
```

`ScheduleAsync` and `ScheduleAtAsync` use the same argument order. Existing cancellation-token
overloads remain unchanged:

```csharp
await scheduler.EnqueueAsync(
    payload,
    groupId: tenantId,
    cancellationToken: cancellationToken);
```

The group id is persisted even when fair queues are disabled, but it does not affect acquisition in
that configuration. The scheduler emits a one-time warning when it encounters grouped jobs while
fair queues are disabled.

Group ids are supported by direct enqueue and schedule operations and by root members added to an
atomic batch with the grouped `AddToBatch` and `AddToBatchAt` overloads. Continuation and recurring
enqueue operations do not currently accept a group id.

## Configuration

Enable the default policy:

```csharp
services.AddMyAppJobs(options =>
{
    options.UseFairQueues();
});
```

Or configure its thresholds:

```csharp
services.AddMyAppJobs(options =>
{
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
| `ConcurrencyShareThreshold` | `0.10` | A group is potentially noisy when its in-flight share is strictly greater than this value. Must be in `(0, 1]`. |
| `MinInflightForNoisy` | `30` | Minimum non-expired in-flight jobs in a group before it can be noisy. Must be positive. |
| `GroupRoundRobin` | `true` | Rotate groups using last-served cursors. When `false`, only noisy-neighbor prioritization applies. |

The configured values are copied to an immutable `FairQueuePolicy` on each
`JobAcquisitionRequest`. A `null` policy uses the normal acquisition path.

## Persistence

### Job records

Durable job representations include a nullable `GroupId` with a maximum length of 128. EF Core and
LinqToDB configure this group-state index in addition to the existing acquisition indexes:

```text
(QueueName, State, GroupId)
```

### Group cursor records

Distributed EF Core and LinqToDB acquisition use:

```text
immediate_fair_queue_groups
  QueueName             composite primary key
  GroupId               composite primary key
  LastServedSequence    bigint
  ConcurrencyStamp      concurrency token
```

Cursor rows are created lazily. Enqueuing a grouped job does not create one. When a grouped job is
successfully claimed with `GroupRoundRobin` enabled:

1. the provider reads the greatest `LastServedSequence` currently visible in the queue;
2. it assigns the selected group that value plus one;
3. it inserts the cursor if this is the group's first service, or updates the existing cursor;
4. it commits the cursor mutation and job claim in the same transaction.

Concurrent scheduler nodes may assign the same sequence to different groups. Such values are
temporary ordering ties: job ownership remains protected by the job concurrency token, and later
claims advance the tied groups.

Claims from the same group coordinate through that group's cursor row. There is no queue-wide
mutable sequence row, so claims for unrelated groups do not contend on shared cursor state.

When a group has no `Pending`, `Scheduled`, or `Active` jobs left in the queue, its cursor is removed.
A later backlog for that group is treated as never served. Cleanup is best-effort fairness metadata
maintenance and cannot invalidate an already committed job transition.

When `GroupRoundRobin` is disabled, acquisition does not create or advance cursor records.
Ungrouped jobs never create cursor records.

### Schema management

EF Core applications provide their own migration for:

- the nullable `GroupId` job column;
- the `(QueueName, State, GroupId)` index;
- the `immediate_fair_queue_groups` table.

LinqToDB schema bootstrap creates the group column, index, and cursor table for new databases.
Existing databases require the equivalent additive schema update.

## Acquisition

Fair acquisition remains queue-local and honors the existing:

- request batch size;
- queue capacity;
- per-job-name capacity;
- lease ownership;
- retry state;
- optimistic concurrency checks.

### Fast paths

When fairness is disabled, the provider uses the normal candidate query and ordering:

```text
DueAt, CreatedAt, Id
```

The same path is used for a queue with no eligible grouped jobs, even when fairness is enabled.

### In-flight measurement

Expired leases are reclaimed before fairness is measured. Only active jobs with an unexpired lease
contribute to in-flight counts:

```text
effectiveGroupId(job) = job.GroupId ?? job.Id

inflight[g] = count(
  State == Active
  && LeaseExpiresAt > now
  && QueueName == queue
  && effectiveGroupId(job) == g)

N = total non-expired Active jobs in the queue

noisy(job) =
  job.GroupId != null
  && inflight[effectiveGroupId(job)] >= MinInflightForNoisy
  && inflight[effectiveGroupId(job)] / N > ConcurrencyShareThreshold
```

Using the job id as the effective group for an ungrouped job keeps null group ids independent rather
than coalescing every ungrouped job into one synthetic group. Ungrouped jobs are always quiet. Among
noisy groups, the group with fewer in-flight jobs sorts first.

### Candidate ranking

For each available acquisition slot, the provider considers the head eligible job from every
group, plus the first eligible ungrouped job. Candidates are ranked by:

```text
candidateRank = row_number(
  partition by effectiveGroupId(job)
  order by DueAt, CreatedAt, Id)
```

Only rows with `candidateRank == 1` enter the fairness ranking:

1. quiet before noisy;
2. among noisy groups, lower in-flight count first;
3. when `GroupRoundRobin` is enabled, lower `LastServedSequence` first;
4. `DueAt`, `CreatedAt`, and `Id`.

A group without a cursor is treated as never served and sorts ahead of already-served groups. After
each successful grouped claim, the provider advances that group's cursor and re-ranks candidates
for the next slot.

With `GroupRoundRobin` disabled, the last-served comparison is omitted. Quiet groups still precede
noisy groups, and the normal due-time order breaks the remaining ties.

The effective-group pseudo-SQL describes the required semantics without grouping nulls together.
Production providers keep ungrouped jobs on their independent fast path and apply grouped head
selection only to non-null group ids; they do not need to materialize job ids as group keys.

## Provider behavior

| Provider/topology | Fair-queue behavior |
| --- | --- |
| In-memory | Candidate selection, job claim, and cursor advancement occur under the existing in-memory lock. |
| EF Core, distributed | Uses transactional SQL cursor rows and optimistic job claims. |
| LinqToDB, distributed | Uses the same cursor semantics with transactions and compare-and-swap updates. |
| Single-server | The in-memory primary performs fair selection; the durable replica mirrors the selected job ids. |
| Redis, distributed | Persists `GroupId`, but fair acquisition throws `NotSupportedException`. |

In single-server mode, fair cursor state is intentionally in memory. The EF Core or LinqToDB
replica's `immediate_fair_queue_groups` table therefore remains empty because the durable replica
does not perform a second acquisition decision.

Distributed EF Core and LinqToDB fair acquisition select and claim one slot at a time so each slot
observes the cursor advanced by the previous claim. The cursor write is part of the existing
per-job claim transaction, while each additional slot requires candidate selection to run again.
Queues without eligible grouped work retain the normal batched acquisition query.

## Dashboard

The dashboard job list and job details display `GroupId` when it is present. The job query API does
not currently filter by group.

## Verification

The test suite covers:

- fairness with acquisition capacity one and deep backlogs;
- interleaving within a multi-job acquisition;
- cross-instance EF Core and LinqToDB claims;
- concurrent cursor ties and same-group contention;
- SQL collation behavior;
- noisy-group classification and expired leases;
- `GroupRoundRobin = false`;
- unchanged ungrouped ordering;
- queue, job-name, and request capacity limits;
- cursor removal and cleanup failures;
- disabled-policy warnings and group-id validation;
- Redis provider rejection;
- single-server selection and replica mirroring;
- dashboard group display and serialization.

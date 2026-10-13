# Fair queues

Fair queues stop one large group (typically a tenant) from starving quieter groups. They change only
*which* eligible job is claimed next; they never serialize a group, throttle it, or change capacity
and lease rules.

## Inputs

- **`JobRecord.GroupId`.** Set through the `groupId` scheduling overloads. `NormalizeGroupId` turns
  null or whitespace into `null` and rejects ids longer than 128 characters. Group ids are persisted
  even when fair queues are disabled; the scheduler then logs a one-time warning
  (`GroupedJobsAcquiredWithoutFairQueues`) when it acquires grouped jobs.
- **`UseFairQueues(...)`** binds `FairQueueOptions` (`ConcurrencyShareThreshold` 0.10,
  `MinInflightForNoisy` 30, `GroupRoundRobin` true). Each `JobAcquisitionRequest` carries an immutable
  `FairQueuePolicy`, or `null` when fairness is off. Providers must honour a non-null policy.

## Selection

Per queue and per acquisition slot (in-memory's `AcquireQueueFairly` is the reference
implementation):

1. If no grouped job is eligible (due, and its job name has capacity), use the ordinary path:
   `DueAt`, `CreatedAt`, `JobHandle`.
2. Otherwise the candidates are the head eligible job of every group plus the head ungrouped job.
3. Rank the candidates:
   - quiet before noisy;
   - among noisy groups, fewer in-flight jobs first;
   - with `GroupRoundRobin`, lower last-served sequence first (no cursor, or ungrouped, counts as 0);
   - then `DueAt`, `CreatedAt`, `JobHandle`.

   A group is **noisy** when its non-expired active count is at least `MinInflightForNoisy` *and* its
   share of the queue's active jobs exceeds `ConcurrencyShareThreshold`. Ungrouped jobs are never
   noisy.
4. Claim the winner, decrement capacities, and with round-robin give its group the next sequence
   (queue maximum + 1). Then re-rank for the next slot.

A group's cursor is removed once it has no pending, scheduled, or active jobs left, so a returning
group starts without cursor debt. Providers do this on terminal transitions (complete, terminal
failure, cancel).

## Providers

| Provider | Implementation |
| --- | --- |
| In-memory | Selection, claim, and cursor update under the storage lock. |
| Single-server | The in-memory primary decides; the durable store only mirrors the claimed ids, so its cursor table stays empty. |
| EF Core / LinqToDB (distributed) | Claim one slot at a time so each slot sees the previous cursor update. Cursors are rows in `immediate_fair_queue_groups`, updated in the claim transaction; clean-up runs after commit. Concurrent nodes may tie on a sequence; job ownership is still protected by the job's concurrency stamp. |
| Redis | Lua scripts maintain job-name indexes and queue-level cursors (below). |

### Redis state

| Key (under the storage prefix) | Type | Contents |
| --- | --- | --- |
| `fair:due:{job name length}:{job name}:{group}` | sorted set | Due members of one group, same score and member as `due:{job name}`. |
| `fair:ungrouped:{job name}` | sorted set | Due members without a group. |
| `fair:groups:{job name}` | set | Groups that currently have due members. |
| `fair:active:{job name}` | hash | Active job count per group (`''` for ungrouped). |
| `fair:cursor:{queue}` | hash | Last-served sequence per group. |

- **Shared helpers.** `RedisScripts.FairQueueFunctions` is a Lua prelude prepended to every script
  that touches this state.
- **Index maintenance.** Every script that adds a job to `due:{job name}` (enqueue, lease reclaim,
  fail-with-retry, `RetryAsync`, recurring materialization, trigger) also indexes it. Claims,
  cancellation, and `RetryAsync` remove the old member, and stale members are cleaned lazily during
  selection.
- **Active counts.** Claims increment them; complete, fail, cancel, and lease reclaim decrement them.
  Acquisition uses `active:job:{job name}` lease-expiry indexes to count only unexpired leases.

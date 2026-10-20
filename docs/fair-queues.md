# Fair queues

Fair queues stop one large group (typically a tenant) from starving quieter groups. They change only
*which* eligible job is claimed next; they never serialize a group, throttle it, or change capacity
and lease rules.

## Inputs

- **`JobRecord.GroupId`.** Set through the `groupId` scheduling overloads. `NormalizeGroupId` turns
  null or whitespace into `null` and rejects ids longer than 128 characters. Group ids are persisted
  independently of whether fair selection is enabled, so changing policy does not require rewriting
  invocations. The scheduler logs when it acquires grouped jobs with fairness disabled.
- **`UseFairQueues(...)`** configures noisy-group thresholds and round-robin selection through
  `FairQueueOptions`. Each `JobAcquisitionRequest` carries an immutable `FairQueuePolicy`, or `null`
  when fairness is off. Providers must honour a non-null policy.

## Selection

Per queue and per acquisition slot:

1. If no grouped job is eligible (due, and its job name has capacity), use the ordinary path:
   `DueAt`, `CreatedAt`, `JobHandle`.
2. Otherwise the candidates are the head eligible job of every group plus the head ungrouped job.
3. Rank the candidates:
   - quiet before noisy;
   - among noisy groups, fewer in-flight jobs first;
   - with `GroupRoundRobin`, lower last-served sequence first (no cursor, or ungrouped, counts as 0);
   - then `DueAt`, `CreatedAt`, `JobHandle`.

   A group is **noisy** when its owned active count is at least `MinInflightForNoisy` *and* its
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
| Single-server | The durable provider selects and commits claims, including acquisition controls and cursor state. The in-memory primary mirrors committed ownership. |
| EF Core / LinqToDB (distributed) | Claim one slot at a time so each slot sees the previous cursor update. Cursors are rows in `immediate_fair_queue_groups`, updated in the claim transaction; clean-up runs after commit. Concurrent nodes may tie on a sequence; job ownership is still protected by the job's concurrency stamp. |
| Redis | Lua scripts maintain job-name indexes and queue-level cursors (below). |

### Redis state

Maintain grouped and ungrouped due indexes per definition and round-robin cursors per queue.
Every script affecting due membership must update the fair indexes in the same atomic operation.
Claims, completion, failure, cancellation, and lease reclaim also keep active-group accounting
consistent. Durable selection counts only unexpired leases and cleans stale members against
invocation state. In-memory selection counts all active jobs because process ownership does not expire.

Share fair-selection and index-maintenance rules across scripts so ordinary and recurring enqueue,
trigger, retry, and recovery paths obey the same policy. Key definitions and Lua helpers remain in
the Redis provider; the rules above define the behavior every provider must preserve.

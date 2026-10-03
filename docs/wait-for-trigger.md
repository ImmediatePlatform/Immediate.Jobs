# Design: Wait for trigger

**Create a job durably now, run it when told to. One primitive for transactional enqueue and
deferred parameters.**

Status: Approved v0.2 · Date: 2026-10-01 · Target: .NET 8+ · Package: `Immediate.Jobs` (core).

---

## 1. Positioning

Two problems turned out to need the same mechanism:

- **Transactional enqueue.** When the application database and jobs storage are separate, a direct
  enqueue can't be atomic with the application transaction. Enqueue first, and a rollback leaves a job
  that sees missing data. Commit first, and a crash loses the job.
- **Deferred parameters.** Some jobs have to exist (in a batch, a graph, or as a handle given to a
  user) before their input is known.

The previous outbox design solved the first with prepared envelopes, a consumer-owned outbox table, a
relay, and delivery receipts. This design replaces all of that with one idea: **the job is always
committed straight away, in a state where it won't run until something triggers it.** There's no
second transaction to coordinate, because the jobs-storage write happens first and is independently
durable. The application transaction then decides whether the job gets triggered or cleaned up.

```text
WaitForTriggerAsync   job committed in jobs storage, not acquirable
UpdateParametersAsync optionally replace the payload (only while waiting)
TriggerAsync          job released: Pending / Scheduled / AwaitingContinuation
CancelAsync           job abandoned (existing API)
```

## 2. Goals and non-goals

### Goals

- Create a job durably in a non-runnable `WaitingForTrigger` state, with or without a payload, with or
  without continuation parents, standalone or as part of a batch.
- Release it with `TriggerAsync`, optionally choosing when it runs, with a `TryTriggerAsync` variant
  for callers that expect the trigger to race.
- Replace the payload with `UpdateParametersAsync` any number of times while it is waiting.
- Document the transactional-enqueue (outbox) pattern built on these primitives, with no outbox table,
  relay, or receipts.
- Stay reflection-free, trimming-safe, and AOT-safe, with no relational schema change.

### Non-goals

- Automatic expiry of never-triggered jobs.
- A built-in reconciler. Applications decide how to resolve stale waiting jobs (§5.3).
- Partial payload patching or untyped JSON submission.
- Triggering or updating from the dashboard.
- Exactly-once execution. Handlers must still tolerate retries.

## 3. Lifecycle

`JobState.AwaitingParameters` is renamed to `WaitingForTrigger`. It keeps numeric value `1`, and every
provider persists `JobState` numerically, so stored data needs no migration. The state is already
treated as non-acquirable and non-terminal by all providers, because it was reserved for this.

`WaitingForTrigger` is a gate independent of dependencies, alongside `RemainingDependencies`. The two
are evaluated separately:

| Event | Dependencies remaining | Result |
| --- | ---: | --- |
| Create | Any | `WaitingForTrigger` |
| `UpdateParametersAsync` | Any | stays `WaitingForTrigger`, payload replaced |
| Parent settles while waiting | Any | stays `WaitingForTrigger`, counter updated |
| Parent trigger condition fails while waiting | Any | `Skipped` (existing semantics) |
| `TriggerAsync` | > 0 | `AwaitingContinuation` |
| `TriggerAsync` | 0 | `Pending`, or `Scheduled` if `DueAt` is in the future |
| `CancelAsync` | Any | `Cancelled` (existing semantics, batch counters and edges settle) |

`Pending` keeps its current meaning: queued, not yet picked up. Nothing is "uncommitted". A waiting
job is fully durable and visible in monitoring. It just won't run yet.

Releasing a continuation already sets `DueAt` to the later of the job's own `DueAt` and the release
time plus the edge delay. Because of that, a delay chosen at trigger time and a parent that settles
later combine correctly: the job runs when both have passed.

## 4. API

### 4.1 Creating waiting jobs

Added to `JobScheduler<TPayload>` and `IJobScheduler<TPayload>`:

```csharp
// Standalone
ValueTask<JobHandle> WaitForTriggerAsync(TPayload payload, CancellationToken cancellationToken = default);
ValueTask<JobHandle> WaitForTriggerAsync(TPayload payload, string groupId, CancellationToken cancellationToken = default);
ValueTask<JobHandle> WaitForTriggerAsync(CancellationToken cancellationToken = default);
ValueTask<JobHandle> WaitForTriggerAsync(string groupId, CancellationToken cancellationToken = default);

// Waiting for both a trigger and parents
ValueTask<JobHandle> WaitForTriggerAsync(
    TPayload payload,
    IReadOnlyList<ContinuationHandle> parents,
    ContinuationTrigger on = ContinuationTrigger.Success,
    CancellationToken cancellationToken = default);
ValueTask<JobHandle> WaitForTriggerAsync(
    TPayload payload,
    IReadOnlyList<ContinuationHandle> parents,
    string groupId,
    ContinuationTrigger on = ContinuationTrigger.Success,
    CancellationToken cancellationToken = default);
ValueTask<JobHandle> WaitForTriggerAsync(
    IReadOnlyList<ContinuationHandle> parents,
    ContinuationTrigger on = ContinuationTrigger.Success,
    CancellationToken cancellationToken = default);
ValueTask<JobHandle> WaitForTriggerAsync(
    IReadOnlyList<ContinuationHandle> parents,
    string groupId,
    ContinuationTrigger on = ContinuationTrigger.Success,
    CancellationToken cancellationToken = default);
```

- **Overload clash when `TPayload` is `string`.** `WaitForTriggerAsync(string groupId)` and
  `WaitForTriggerAsync(TPayload payload)` collide in that case. It is uncommon, and callers can tell
  them apart with named arguments (`payload:` / `groupId:`).
- **The group is fixed at creation.** It is not changed by updates or the trigger.
- **Parent overloads use the same parent rules as `ScheduleAfterAsync`.** Job and batch handles can be
  mixed, an empty or duplicate parent list is rejected, and they require graph-capable storage.
- **Jobs without parameters.** The missing payload is stored internally as the empty string, which no
  serializer produces, so `JobRecord.Payload` stays `required string` and no column changes.
- **Context and trace are captured at creation** and are not changed by updates or the trigger.

### 4.2 Updating parameters

```csharp
ValueTask UpdateParametersAsync(JobHandle job, TPayload payload, CancellationToken cancellationToken = default);
```

The update is only allowed while the job is `WaitingForTrigger`, and can be repeated. The state check
and the write happen as one atomic operation. It throws if the job doesn't exist
(`KeyNotFoundException`), or if it belongs to a different scheduler or is no longer waiting
(`ImmediateJobException`).

Setting parameters isn't expected to be contended. The typical caller is a business process
supplying a value a user provided, so an update on a job that is no longer waiting is a usage error
rather than a race to tolerate.

### 4.3 Triggering

```csharp
ValueTask TriggerAsync(JobHandle job, CancellationToken cancellationToken = default);
ValueTask TriggerAsync(JobHandle job, TimeSpan delay, CancellationToken cancellationToken = default);
ValueTask TriggerAsync(JobHandle job, DateTimeOffset at, CancellationToken cancellationToken = default);

ValueTask<bool> TryTriggerAsync(JobHandle job, CancellationToken cancellationToken = default);
ValueTask<bool> TryTriggerAsync(JobHandle job, TimeSpan delay, CancellationToken cancellationToken = default);
ValueTask<bool> TryTriggerAsync(JobHandle job, DateTimeOffset at, CancellationToken cancellationToken = default);
```

Triggering is the one operation that may be contended, depending on the outbox pattern the
application uses. For example, the request path and a reconciler can both try to release the same job.
Both methods check and release in one atomic operation, so there is no time-of-check/time-of-use gap.
They differ only in how they report a job that is no longer waiting:

| Situation | `TriggerAsync` | `TryTriggerAsync` |
| --- | --- | --- |
| Waiting, parameters supplied | released | released, returns `true` |
| Already triggered, cancelled, or skipped | throws `ImmediateJobException` | returns `false` |
| Waiting, no parameters supplied | throws `ImmediateJobException` | throws `ImmediateJobException` |
| Job doesn't exist | throws `KeyNotFoundException` | throws `KeyNotFoundException` |
| Wrong scheduler, or negative delay | throws | throws |

- **`TriggerAsync` enforces correct usage** through exceptions. Callers that know triggering races in
  their setup use `TryTriggerAsync` and avoid exceptions in the expected case.
- **A missing payload always throws.** It is a usage error, not a race: nothing else is expected to be
  supplying parameters at the same moment as a trigger.
- **Timing is set at trigger time.** "Run 10 minutes after the order commits" means 10 minutes after
  the trigger. `DueAt` is set by the trigger, and §3 describes how it combines with parents.

### 4.4 Batches

Batches keep their existing builder. The only change is how they are committed:

```csharp
await using var batch = batchScheduler.Begin();

var reserve = reserveInventory.Enqueue(new(order.Id), batch);
var charge = chargePayment.Enqueue(new(order.Id), batch);
createShipment.ScheduleAfter(new(order.Id), [reserve, charge]);

BatchHandle handle = await batch.CommitWaitingForTriggerAsync(cancellationToken);
// ...
await batchScheduler.TriggerAsync(handle, cancellationToken);
```

- `Batch.CommitWaitingForTriggerAsync()` commits the whole graph atomically, like `CommitAsync()`,
  but with every member in `WaitingForTrigger` and the batch in the new `BatchState.WaitingForTrigger`.
  `RemainingDependencies` and edges are unchanged. Follow-up batches (`Begin(parentBatch)`) work the
  same way.
- `BatchScheduler.TriggerAsync(BatchHandle)` and `TryTriggerAsync(BatchHandle)` have the same
  semantics as their job counterparts. In one transaction they move the batch to `Executing` and
  release every waiting member following §3. Triggering a member of a waiting batch individually
  throws, which keeps the batch atomic. Batch members are always added with a payload, so a batch
  can't be missing parameters.
- `UpdateParametersAsync` works on any waiting member through its `JobHandle`. That handle becomes
  readable once the batch is committed.
- Member timing is set at creation as it is today (`Schedule(payload, batch, delay)`). A member whose
  `DueAt` has passed by the time of the trigger becomes `Pending`.
- `BatchScheduler.CancelAsync` already cancels waiting members. It moves a waiting batch to
  `Cancelled`.

`BatchState.WaitingForTrigger` is added at the end of the enum (value `4`), so persisted values don't
shift.

### 4.5 Not covered

The running-job overloads (`JobDetails currentJob` / `ContinuationOptions`) get no waiting variant.
Those run inside jobs storage and don't have a transaction problem to solve.

## 5. Outbox pattern (documentation)

Immediate.Jobs ships no outbox machinery. The docs explain how to get outbox guarantees from the
primitives above.

### 5.1 Flow

```csharp
JobHandle job = await sendEmail.WaitForTriggerAsync(new(order.Id), cancellationToken);   // 1

order.EmailJob = job.Value;                                                              // 2
appDb.Orders.Add(order);
await appDb.SaveChangesAsync(cancellationToken);

await sendEmail.TriggerAsync(job, cancellationToken);                                    // 3
```

1. The job is committed in jobs storage but can't run.
2. The application stores the handle in the same transaction as its data. That row is the proof that
   the transaction committed.
3. The job is released after the commit.

### 5.2 Failure windows

| Crash or failure | State left behind | Recovery |
| --- | --- | --- |
| Before step 1 completes | Nothing | Nothing to do |
| Between 1 and 2, or the transaction rolls back | Waiting job, no application row | Cancel it (directly on rollback, or during reconciliation) |
| Between 2 and 3 | Waiting job, application row exists | Trigger it during reconciliation |
| After 3 | Released job | Normal execution |

The job can only run once the application row is known to exist. That is the same guarantee an outbox
gives, without a second table or relay.

### 5.3 Reconciliation

Applications choose how to resolve stale waiting jobs. Only the application knows what counts as a
committed row, and some will prefer different grace periods, alerting, or manual review. The docs show
one approach, a recurring job:

```csharp
[Handler, Job(Name = "reconcile-email-jobs", Cron = "*/5 * * * *")]
public sealed partial class ReconcileEmailJobs(
    IJobMonitor monitor,
    SendEmail.Scheduler sendEmail,
    AppDb appDb,
    TimeProvider timeProvider)
{
    public sealed record Payload;

    private async ValueTask HandleAsync(Payload _, CancellationToken cancellationToken)
    {
        var stale = await monitor.QueryJobsAsync(new()
        {
            JobName = sendEmail.JobName,
            State = JobState.WaitingForTrigger,
            CreatedBefore = timeProvider.GetUtcNow().AddMinutes(-5),
        }, cancellationToken);

        foreach (var job in stale)
        {
            if (await appDb.Orders.AnyAsync(o => o.EmailJob == job.JobHandle.Value, cancellationToken))
                _ = await sendEmail.TryTriggerAsync(job.JobHandle, cancellationToken);
            else
                await sendEmail.CancelAsync(job.JobHandle, cancellationToken);
        }
    }
}
```

The request path may trigger the same job while the reconciler is running, so the reconciler uses
`TryTriggerAsync`. Whichever runs second gets `false` instead of an exception. The grace period must be longer
than the longest application transaction. Otherwise a job whose transaction is about to commit could
be cancelled. The only core change this needs is a `CreatedBefore` filter on `JobQuery`.

The docs also cover batches (store the `BatchHandle`, trigger with `BatchScheduler.TriggerAsync`) and
the advice that handlers must stay idempotent.

## 6. Storage

Following [`storage-consolidation.md`](storage-consolidation.md), there are no new interfaces or
capability flags. The new members fold into the two existing interfaces:

```csharp
public interface IJobStorage
{
    // ...existing members...

    ValueTask UpdatePayloadAsync(JobHandle job, string expectedJobName, string payload, CancellationToken cancellationToken = default);

    // true = released by this call; false = no longer waiting. Missing job, wrong name, or missing payload throw.
    ValueTask<bool> TryTriggerAsync(JobHandle job, string expectedJobName, DateTimeOffset dueAt, CancellationToken cancellationToken = default);
}

public interface IJobGraphStorage : IJobStorage
{
    // ...existing members...

    ValueTask<bool> TryTriggerBatchAsync(BatchHandle batch, CancellationToken cancellationToken = default);
}
```

Storage exposes only the `Try` form. The scheduler's `TriggerAsync` calls it and throws
`ImmediateJobException` on `false`, so providers implement one atomic operation.

- **Creation reuses the existing writes.** Waiting jobs go through `EnqueueAsync`, waiting
  continuations through `EnqueueContinuationAsync`, and waiting batches through `EnqueueBatchAsync`, each
  with records already in `WaitingForTrigger` (and the batch in `BatchState.WaitingForTrigger`).
  Providers already store whatever state a record carries, so no new insert methods are needed.
- **No capability checks for standalone jobs.** Every provider supports waiting jobs. The parent and
  batch variants use the existing `RequireGraph` guard, like the rest of the graph API.
- **Graph code must preserve the new state.** Parent settlement, already-terminal-parent evaluation,
  and dynamic continuation insertion must keep `WaitingForTrigger` rather than overwrite it. Today they
  assume only `AwaitingContinuation` can have pending dependencies.
- **Trigger conditions are evaluated at trigger time.** A job whose parents all settled while it was
  waiting has its incoming trigger conditions checked by the trigger. It becomes `Skipped` if they
  fail, using the same evaluation release uses today.
- **Batch code must handle the new batch state.** Counter maintenance, batch cancellation, and
  retention purging must account for `BatchState.WaitingForTrigger`.

| Provider | Support |
| --- | --- |
| In-memory | Everything, under the existing lock. |
| EF Core | Everything. Updates the existing payload, state, and due columns under current concurrency handling. |
| LinqToDB | Everything, same as EF Core. |
| Single-server | Everything. Writes durable storage first, then the memory primary. |
| Redis | Standalone jobs. Lua moves the job between state sets and the due index. No parents or batches, since Redis is not graph-capable. |

## 7. Dashboard and monitoring

- Rename the job state and add the batch state in the dashboard's string contract (`contracts.ts`,
  fixtures, tests). This is a visible change for anyone consuming the monitoring JSON.
- Show "Waiting for trigger" for jobs and batches, and for a job without parameters, "Parameters not
  supplied" instead of an empty payload.
- The dashboard offers no trigger or update actions. Those stay with the application's own endpoints.
- Add `CreatedBefore` to `JobQuery`.

## 8. Test plan

- **Waiting jobs:**
  - A waiting job is never acquired.
  - After a trigger it runs with the latest payload, context from creation, and `DueAt` from the
    trigger.
  - A trigger with a delay becomes `Scheduled`; a trigger with no delay becomes `Pending`.
- **Triggering:**
  - Every row of the §4.3 table, for both `TriggerAsync` and `TryTriggerAsync`.
  - Concurrent `TryTriggerAsync` calls produce exactly one `true`.
  - Concurrent `TriggerAsync` calls produce one success and exceptions for the rest.
  - A trigger racing an update or cancel produces one consistent outcome.
- **Updates:**
  - Updates succeed repeatedly while waiting.
  - After a trigger, cancellation, or skip they throw, and the payload is unchanged.
  - A missing job or wrong scheduler throws.
- **Waiting continuations:**
  - Parents settle before the trigger, and after it.
  - Mixed job and batch parents, fan-in, and a trigger delay combined with a late parent.
  - Failed trigger conditions detected at trigger time and while waiting.
  - Already-terminal parents at creation.
- **Batches:**
  - Commit leaves the batch in `BatchState.WaitingForTrigger`; a trigger moves it to `Executing` and
    releases every member atomically.
  - `TriggerAsync` / `TryTriggerAsync` on a batch that is already triggered or cancelled.
  - Follow-up batches, counters, cancellation while waiting, and retention.
- **Durability:** waiting jobs, continuations, and batches survive restart, including single-server
  recovery.
- **Pattern:** a sample exercises every row of §5.2, including the request path and the reconciler
  triggering the same job concurrently.
- **Providers:**
  - in-memory, EF Core, LinqToDB, and single-server for everything;
  - Redis for standalone jobs;
  - non-graph storage rejects the parent and batch variants before writing.
- **Rename:** existing rows with state `1` read back as `WaitingForTrigger`.

## 9. Decisions

1. **One state and three verbs** (wait, update, trigger) cover both transactional enqueue and deferred
   parameters. The separate outbox and awaiting-parameters designs are dropped.
2. **Jobs are always committed.** There is no "uncommitted" state, just an explicit gate.
3. **`Pending` keeps its meaning** (queued, not yet picked up). `AwaitingParameters` is renamed rather
   than adding a new state, keeping value `1`.
4. **Parameters can be updated repeatedly** while waiting. The group is fixed at creation.
5. **Timing is set at trigger time** for standalone jobs and continuations. Batch members keep their
   creation-time timing.
6. **Waiting continuations are in v1**, using the same two-gate model as batch members.
7. **Batches trigger as a unit** and get their own `BatchState.WaitingForTrigger`.
8. **Only triggering gets a non-throwing form.**
   - Setting parameters isn't expected to be contended, so `UpdateParametersAsync` throws when the job
     isn't waiting.
   - Triggering can race, depending on the outbox pattern. `TriggerAsync` throws on a job that is no
     longer waiting, which enforces correct use, while `TryTriggerAsync` returns `false` for callers
     that expect the race.
   - A missing payload always throws.
9. **No built-in reconciler.** Applications decide how to resolve stale waiting jobs, guided by the
   outbox-pattern docs.
10. **The `TPayload == string` overload clash is accepted.** Named arguments resolve it.

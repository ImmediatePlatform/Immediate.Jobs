# Batches and continuations

Graph features need `IJobGraphStorage` (in-memory, single-server, EF Core, LinqToDB). Redis does not
support them.

## Handles

- `JobHandle` and `BatchHandle` are records wrapping opaque string ids; both derive from the closed
  record `ContinuationHandle`, so APIs that accept "a job or a batch" take a `ContinuationHandle`.
- `BatchJobHandle` refers to a job inside an uncommitted `Batch`. Its `JobHandle` throws until the
  batch is committed, because the job doesn't exist yet.

## Durable model

- **Edges** (`JobContinuationEdge`): a child job, exactly one parent (a job *or* a batch), a
  `ContinuationTrigger`, and a `Delay`.
- **Counters** on the child record: `RemainingDependencies` (unsettled parents) and
  `FailedDependencies`.
- **Batches** (`BatchRecord`): counters (`TotalJobs`, `PendingCount`, succeeded, failed, cancelled,
  skipped), timestamps, and a `BatchState`.

Nothing executable is stored: a continuation is a fully serialized `JobRecord` parked until its
parents settle.

### Settlement

When a parent reaches a terminal state, each unsettled outgoing edge settles: the child's
`RemainingDependencies` decrements (and `FailedDependencies` increments for a failed parent). When it
reaches zero, the child's incoming triggers are evaluated:

| Trigger | Child runs when |
| --- | --- |
| `Success` | every parent succeeded |
| `Failure` | every parent is terminal and at least one failed |
| `Complete` | every parent is terminal |

A child whose condition fails becomes `Skipped`, which is itself terminal and propagates to its own
children. A released child's `DueAt` becomes the later of its own `DueAt` and the release time plus
the largest incoming edge delay, then it moves to `Pending` or `Scheduled`.

Inserting a continuation whose parents are already terminal settles those edges immediately, so
ordering between parent completion and child insertion never matters.

### Batch state

A batch is `Executing` until `PendingCount` reaches zero, then becomes `Failed` if any member failed,
else `Cancelled` if any was cancelled, else `Succeeded`. A terminal batch settles edges whose parent
is the batch. `CancelBatchAsync` cancels every non-terminal member; `DeleteBatchAsync` only accepts
terminal batches.

## Building graphs

- **`BatchScheduler.Begin()`** returns a `Batch` buffer. Generated schedulers add to it with
  `Enqueue(payload, batch)`, `Schedule(payload, batch, …)`, and `ScheduleAfter(payload, jobs, …)`.
  `CommitAsync()` inserts the whole graph atomically; disposing an uncommitted batch discards it.
- **Follow-up batches.** `Begin(parentBatch[, on])` adds an edge from each parent batch to every
  *root* member of the new batch.
- **Standalone continuations.** `scheduler.ScheduleAfterAsync(payload, parents, …)` inserts one child
  with edges to existing jobs or batches.
- **From a running job.** The `JobDetails currentJob` overloads take a `ContinuationOptions`:
  - `Detached`: an ordinary job outside the batch.
  - `BesideContinuations`: a new batch member running in parallel.
  - `BeforeContinuations`: a new batch member that the current job's existing waiters must also wait
    for. The waiters gain an extra dependency (a "splice").

  `EnqueueAsync`/`ScheduleAsync` with `JobDetails` add a member to the current batch immediately
  through `AddBatchJobAsync`; they reject `Detached` and require the current job to be in a batch.
  `ScheduleAfter(payload, currentJob, …)` returns synchronously and buffers into the
  `JobExecutionBuffer`; the buffer is sealed when the handler returns and committed together with
  completion by `CompleteWithContinuationsAsync`, so a failed attempt discards it.

## Wait for trigger

A job or batch can be committed in a parked state and released later. This supports transactional
enqueue (the outbox pattern: create the job, commit the application transaction storing its handle,
then trigger) and deferred parameters.

- **Creating.** `WaitForTriggerAsync` overloads on the generated scheduler create a
  `WaitingForTrigger` job, with or without a payload, a group, or continuation parents. A job without
  parameters stores an empty payload, which no serializer produces, and can't be triggered until
  `UpdateParametersAsync` supplies one. `Batch.CommitWaitingForTriggerAsync()` commits every member
  and the batch as `WaitingForTrigger`.
- **Releasing.** `TriggerAsync`/`TryTriggerAsync` (job) and `BatchScheduler.TriggerAsync`/
  `TryTriggerAsync` (batch) call storage's `TryTriggerAsync`/`TryTriggerBatchAsync`. Storage returns
  `false` when the target is no longer waiting; the `Trigger…` forms turn that into an exception.
  A batch member can only be released by its batch: storage throws if one is triggered as a job.
  Timing passed to a job trigger is measured from the trigger; batch members keep the due times they
  were created with.
- **Two gates.** `WaitingForTrigger` and `RemainingDependencies` are independent:
  - Settling parents of a waiting job updates its counters but leaves it waiting, unless its trigger
    condition fails, in which case it is skipped.
  - Triggering a job with remaining dependencies moves it to `AwaitingContinuation`.
  - Triggering one whose parents have all settled evaluates its triggers and edge delays as release
    would.
- **Provider rules.** Insert-time evaluation, settlement, and splicing must keep `WaitingForTrigger`
  rather than overwrite it with `AwaitingContinuation`, and `BatchState.WaitingForTrigger` is
  non-terminal everywhere. Missing these rules makes a waiting job run early or its counter never move.

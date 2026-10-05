# Job lifecycle

## Records

A job is a `JobRecord` (`Apis/JobRecord.cs`): an opaque string `JobHandle`, the stable `JobName`,
`QueueName`, optional `GroupId`, a JSON `Payload`, an optional context envelope, `State`, `DueAt`,
`CreatedAt`, the current `Attempt`, lease fields (`WorkerId`, `LeaseExpiresAt`), the trace parent
captured at enqueue, and graph fields (`BatchHandle`, `RemainingDependencies`, `FailedDependencies`).

Every attempt also gets a `JobExecutionRecord` row (execution history), whose state is `Active`,
`Succeeded`, `Failed`, `Cancelled`, or `Interrupted` (the lease expired before an outcome was
recorded). Providers create a *synthetic* execution row when they need history for an attempt that
never got one, for example when a lease is reclaimed.

`JobState` is persisted **numerically** by every provider. Never reorder or renumber it; add new
members at the end, and rename only when the numeric value keeps its meaning.

## States

| Value | State | Acquirable | Meaning |
| ---: | --- | :-: | --- |
| 0 | `AwaitingContinuation` | no | Waiting for continuation parents (see [batches and continuations](batches-and-continuations.md)). |
| 1 | `WaitingForTrigger` | no | Durably created, released only by `TriggerAsync`/`TryTriggerAsync`. |
| 2 | `Scheduled` | when due | Has a future `DueAt`. |
| 3 | `Pending` | yes | Due and waiting for a worker. |
| 4 | `Active` | no | Claimed by a worker under a lease. |
| 5 | `Succeeded` | — | Terminal. |
| 6 | `Failed` | — | Terminal; retained so it can be retried. |
| 7 | `Cancelled` | — | Terminal. |
| 8 | `Skipped` | — | Terminal; a continuation whose trigger condition was not met, or a recurring occurrence skipped by `OverlapPolicy.Skip`. |

Acquisition only considers `Pending` and `Scheduled` jobs with `DueAt <= now`, plus `Active` jobs
whose lease has expired (they are reclaimed first).

## Attempts, retries, and timeouts

- **Claiming** sets `Active`, increments `Attempt`, assigns the worker and a lease of
  `ImmediateJobsOptions.LeaseDuration`, and opens an execution row.
- **Timeout** comes from `[Job(Timeout = "hh:mm:ss")]`; the handler's token is cancelled when it
  elapses. That fails the attempt only if the handler throws; a handler that ignores the token and
  returns completes the job.
- **Failure** calls `FailAsync` with a `nextRetryAt` while `Attempt < MaxAttempts`, which moves the job
  to `Scheduled` (or `Pending` if already due). After the last attempt the job becomes `Failed`.
- **Backoff** is computed in `JobSchedulingService.GetRetryDelay`: `Fixed` uses `BackoffBase`;
  `Exponential` doubles it per attempt (capped at 2^30); `ExponentialJitter` multiplies that by a
  random factor in `[0.5, 1.5)`.
- **Leases** are renewed by the lease-renewal loop while the handler runs. If a process dies, another
  acquisition reclaims the job once the lease expires, marking the open execution `Interrupted`.
- **Ownership checks.** Every completion, failure, telemetry, and renewal call passes the worker id
  and attempt; storage rejects stale owners, so a reclaimed job can't be completed twice.

## Manual operations

| Operation | Allowed from | Result |
| --- | --- | --- |
| `CancelAsync` | any non-terminal state | `Cancelled`; continuation edges settle as a cancelled parent. |
| `RetryAsync` | `Failed`, `Scheduled` | `Pending`, due now. |
| `DeleteAsync` | terminal states | Removes the job and its history. |
| `UpdatePayloadAsync` | `WaitingForTrigger` | Replaces the payload. |
| `TryTriggerAsync` | `WaitingForTrigger` | Releases the job (see [batches and continuations](batches-and-continuations.md#wait-for-trigger)). |

## Retention

The polling loop purges on `PurgeInterval` (default one hour):

- `PurgeJobsAsync` deletes standalone terminal jobs older than `SucceededRetention` (24 hours) or
  `FailedRetention` (7 days, which also covers `Cancelled` and `Skipped`).
- `PurgeBatchesAsync` (graph storage only) deletes terminal batches, with all their members and
  edges, older than `BatchSucceededRetention` / `BatchFailedRetention`. Batch members are never purged
  individually.

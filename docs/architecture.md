# Architecture

Immediate.Jobs is a reflection-free background job scheduler. A source generator turns each
`[Job]`-annotated Immediate.Handlers handler into a typed scheduler, an invoker, and JSON metadata;
a hosted service acquires due work from a storage provider and runs it.

## Projects

| Project | Ships as | Role |
| --- | --- | --- |
| `src/Immediate.Jobs.Shared` | inside `Immediate.Jobs` | Runtime: schedulers, `JobSchedulingService`, storage contracts, in-memory and single-server storage, monitoring API, telemetry. |
| `src/Immediate.Jobs.Generators` | inside `Immediate.Jobs` | Incremental source generator (Scriban templates in `Templates/`). |
| `src/Immediate.Jobs.Analyzers` | inside `Immediate.Jobs` | `IJOB####` diagnostics and one suppressor. |
| `src/Immediate.Jobs` | `Immediate.Jobs` | Packaging-only project that bundles the three above. |
| `src/Immediate.Jobs.EntityFrameworkCore` | own package | EF Core storage. |
| `src/Immediate.Jobs.LinqToDB` | own package | LinqToDB storage. |
| `src/Immediate.Jobs.Redis` | own package | Redis storage (no batches or continuations). |
| `src/Immediate.Jobs.Dashboard` | own package | Dashboard HTTP API and embedded Vue client. |
| `src/Immediate.Jobs.NodaTime` | own package | NodaTime scheduling overloads and serialization. |
| `src/Immediate.Jobs.Testing` | own package | `JobTestHarness`, `CapturingJobStorage`, and the storage conformance suite. |
| `samples/*` | not packed | Basic, Aspire, distributed Aspire, SQLite, and Native AOT samples. |

## What the generator emits

For each `[Job]` class (see `Templates/Job.sbntxt`), nested in the user's partial class:

- **`Scheduler`**: a scoped `JobScheduler<TPayload>` subclass that also implements
  `IJobScheduler<TPayload>`. A payloadless job's scheduler also implements `IRecurringJobScheduler`
  (dynamic schedules), or `IRecurringJobTrigger` when the job declares a code-defined `Cron`. All scheduling
  logic lives in the base class; the generated type only supplies the job name, queue, JSON metadata,
  and context capture.
- **`Invoker`**: a singleton `IJobInvoker` that deserializes the payload and runs the handler through
  the Immediate.Handlers pipeline.
- **`JobDefinition`**: the runtime description (name, queue, attempts, timeout, backoff, cron,
  overlap and misfire policies, concurrency).
- **`PayloadJsonContext`**: hand-emitted System.Text.Json metadata for the payload and context
  types, so serialization never uses reflection.

`ServiceCollectionExtensions.sbntxt` emits one `Add{Assembly}Jobs()` method per assembly that calls
`AddImmediateJobsCore()` and registers every job. It returns `IImmediateJobsBuilder`, the fluent
configuration surface (`ConfigureStorage`, `ConfigureWorkers`, `UseFairQueues`, `AddHealthCheck`, …).

## Runtime

`JobSchedulingService` (`Internals/JobSchedulingService.cs`) is one hosted service per process:

- **Polling loop.** Each iteration materializes due recurring schedules, builds a
  `JobAcquisitionRequest` from free worker capacity and queue/job concurrency limits, calls
  `IJobStorage.AcquireDueJobsAsync`, and writes the claimed records to an unbounded channel. On
  `PurgeInterval` it also purges job and batch history. It then waits `PollingInterval`.
- **Workers.** `WorkerCount` tasks read the channel and run `ExecuteJobAsync`: a new DI scope, a
  timeout token, an `Activity` linked to the trace captured at enqueue, then the invoker. Success
  completes the job (through `CompleteWithContinuationsAsync` on graph storage, so buffered mid-job
  scheduling commits atomically); failure retries with backoff until `MaxAttempts`.
- **Lease renewal loop.** Renews leases for running jobs so another node doesn't reclaim them.
- **Heartbeat loop.** Persists a `JobServerSnapshot` for the dashboard's servers page.
- **Shutdown.** Completes the channel, lets workers drain for `ShutdownTimeout`, then cancels.

`DrainAsync` runs polling iterations and executes inline until nothing is due; it exists for
deterministic tests (`JobTestHarness` uses it).

All options live in `ImmediateJobsOptions` and are validated with Immediate.Validations.

## Topologies

Selected through `ConfigureStorage(...)`:

| Mode | Storage | Use |
| --- | --- | --- |
| `UseInMemory()` | `InMemoryJobStorage` | Development and tests. Not durable. |
| `UseSingleServer()` | `SingleServerJobStorage` wrapping a durable provider | One scheduler process. The in-memory primary makes every acquisition decision; the durable provider is a write-through copy used for restart recovery. The default when a durable provider is configured without a mode. |
| `UseDistributed()` | the durable provider directly | Several scheduler processes coordinating through the database or Redis. |

## Data flow

```text
scheduler.EnqueueAsync(payload)
  → JobScheduler<T>.CreateRecord (id, serialized payload, captured context, trace parent)
  → IJobStorage.EnqueueAsync
polling loop
  → AcquireDueJobsAsync (Pending/Scheduled → Active, lease, new execution row)
worker
  → Invoker → handler
  → CompleteAsync / CompleteWithContinuationsAsync / FailAsync
```

Graph work (batches, continuations, waiting jobs) adds edges and counters on the way in; see
[batches and continuations](batches-and-continuations.md).

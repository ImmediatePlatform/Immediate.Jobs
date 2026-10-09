# Observability and context

## Context propagation

Ambient request state (tenant, user, culture, …) can flow from the enqueueing scope into the job:

- An application derives from `JobContextExtractor<TContext>` (`Capture()` and `Restore(TContext)`)
  and applies it with `[UsesJobContext<TExtractor>]` on a job, or on a reusable marker attribute that
  jobs carry.
- The generator emits `CaptureContext()` on the job's `Scheduler`. It runs every extractor, serializes
  each value with the generated JSON metadata, and combines the results into one envelope with
  `JobContextEnvelope.AddSlice`/`Create`. The envelope is stored in `JobRecord.Context` and is fixed
  at creation; updates and triggers don't change it.
- The generated `Invoker` reads the envelope (`JobContextEnvelope.Read`) and calls `Restore` on the
  scoped extractor before running the handler. Slices without a matching extractor (for example after
  an extractor was removed) are logged by `LogOrphanedSlices` and ignored.

## Traces

- The scheduler captures `Activity.Current` as `TraceParent`/`TraceState` when it creates a record.
- `JobSchedulingService.ExecuteJobAsync` starts an `ActivitySource("Immediate.Jobs")` consumer
  activity named `job {name}`, **linked** (not parented) to the captured context, with `job.name`,
  `job.queue`, `job.id`, and `job.attempt` tags.
- The execution's trace and span ids are persisted with `SetExecutionTelemetryAsync`, best effort, so
  the dashboard can link an attempt to the configured tracing UI (`JobTelemetryLinks`).

## Metrics

Meter `Immediate.Jobs` (`Internals/JobTelemetry.cs`):

| Instrument | Type |
| --- | --- |
| `jobs.enqueued`, `jobs.succeeded`, `jobs.failed`, `jobs.retried` | counters, tagged `job.name` and `job.queue` |
| `job.duration` | histogram, seconds |
| `acquisition.count`, `workers.active` | observable gauges |

`jobs.enqueued` is emitted when a job is created through a scheduler, including waiting jobs and
recurring occurrences.

## Logging

Every log call is a source-generated `[LoggerMessage]` with a fixed `EventId` from the assembly's
`LibraryEventIds` class. Storage providers log a Debug "…Called" message at the start of each public
operation. Ranges and rules are in [conventions](conventions.md#logging).

## Health checks

`AddHealthCheck(name = "immediate-jobs")` registers `StorageHealthCheck` (`{name}-storage`, which calls
`IJobStorage.IsHealthyAsync` and reports the storage capabilities) and `ServiceHealthCheck`
(`{name}-service`, the scheduler loop's liveness).

## Monitoring API

`JobMonitor` (`Apis/JobMonitor.cs`) is the programmatic surface the dashboard uses. Its read operations
are also exposed through the `IJobMonitor` interface:

- the overview snapshot (counts by state, servers, capabilities, recurring schedules, definition status);
- `GetDefinitionsAsync` for all stored job and recurring configuration, without acquisition evaluation;
- live job acquisition and recurring pause/progress in `GetSnapshotAsync`, bulk-evaluated by storage;
- paged job, execution, and batch queries, plus batch members and graphs;
- cancel, retry, and delete for jobs and batches;
- `PauseDefinitionAsync` and `ResumeDefinitionAsync` for stored job definitions, preserving configured limits;
- pause, resume, and trigger for recurring schedules.

Graph reads return "not available" on non-graph storage instead of throwing.
